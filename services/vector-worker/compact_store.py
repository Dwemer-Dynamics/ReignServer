"""Streaming loader for Reign's pinned Qdrant local archive; same persistence format.

Qdrant 1.14.2 fetches every SQLite blob and holds Python float lists before
building a float64 array. This adapter changes loading only, retaining native
Qdrant persistence, filters and the exclusive archive lock. Insertions reserve
bounded spare rows and correct the legacy duplicate-ID row allocation case.
"""
from __future__ import annotations

import importlib.metadata
import json
import pickle
from copy import deepcopy
from pathlib import Path

import numpy as np
import portalocker
from qdrant_client.http import models
from qdrant_client.local.local_collection import LocalCollection, to_jsonable_python, EPSILON
from qdrant_client.local.qdrant_local import QdrantLocal, META_INFO_FILENAME


class CompactCollection(LocalCollection):
    def __init__(self, *args, **kwargs):
        self.duplicate_storage_rows = 0
        self.loaded_storage_rows = 0
        try:
            super().__init__(*args, **kwargs)
        except Exception:
            if getattr(self, "storage", None) is not None:
                self.storage.close()
            raise

    def load_vectors(self):
        if self.storage is None:
            return
        if self.sparse_vectors or self.multivectors or set(self.vectors) != {""}:
            raise ValueError("Reign compact loader requires one anonymous dense vector")
        size = self.vectors_config[""].size
        connection = self.storage.storage
        # Cursor iteration bounds transient blobs/float objects to one record.
        # No new serialization or migration: these are the same private native
        # PointStruct records consumed by CollectionPersistence.load().
        with connection:
            count = connection.execute("SELECT COUNT(*) FROM points").fetchone()[0]
            capacity = count + min(8192, max(256, count // 8))
            vectors = np.empty((capacity, size), dtype=np.float32)
            missing = np.zeros(count, dtype=bool)
            cursor = connection.execute("SELECT point FROM points")
            loaded = 0
            try:
                for row in cursor:
                    point = pickle.loads(row[0])
                    if not isinstance(point, models.PointStruct):
                        raise ValueError("Invalid persisted point")
                    if point.id in self.ids:
                        # Legacy archives can contain distinct persistence keys
                        # for one PointStruct ID. Preserve native last-row ID
                        # resolution and every original row; expose the count.
                        self.duplicate_storage_rows += 1
                    self.ids[point.id] = loaded
                    self.ids_inv.append(point.id)
                    self.payload.append(to_jsonable_python(point.payload) or {})
                    value = point.vector.get("") if isinstance(point.vector, dict) else point.vector
                    if value is None:
                        vectors[loaded].fill(1)
                        missing[loaded] = True
                    else:
                        array = np.asarray(value, dtype=np.float32)
                        if array.shape != (size,) or not np.isfinite(array).all():
                            raise ValueError("Invalid persisted dense vector")
                        vectors[loaded] = array
                    loaded += 1
            finally:
                cursor.close()
            if loaded != count:
                raise ValueError("Archive changed during compact load")
        self.vectors[""] = vectors
        self.deleted_per_vector[""] = missing
        self.deleted = np.zeros(count, dtype=bool)
        self.loaded_storage_rows = count

    def _add_point(self, point):
        # Native 1.14.2 uses len(ids) as a row index. Legacy duplicate keys
        # make that smaller than the actual row count and can overwrite an
        # unrelated row on the next insert. Allocate by physical row count;
        # this changes no historical persisted records or ID resolution.
        if self.sparse_vectors or self.multivectors or set(self.vectors) != {""}:
            raise ValueError("Unsupported compact insertion vector configuration")
        value = point.vector.get("") if isinstance(point.vector, dict) else point.vector
        size = self.vectors_config[""].size
        if value is None:
            vector = np.ones(size, dtype=np.float32)
        else:
            # Preserve native upsert's float64 normalization before float32
            # assignment; cold loading separately preserves original vectors.
            vector = np.asarray(value, dtype=np.float64)
            if vector.shape != (size,) or not np.isfinite(vector).all():
                raise ValueError("Invalid inserted dense vector")
            if self.vectors_config[""].distance == models.Distance.COSINE:
                norm = np.linalg.norm(vector)
                if norm > EPSILON:
                    vector = vector / norm
        payload = deepcopy(to_jsonable_python(point.payload) or {})
        position = len(self.payload)
        if position >= self.vectors[""].shape[0]:
            capacity = position + min(8192, max(256, position // 8))
            expanded = np.empty((capacity, size), dtype=np.float32)
            expanded[:position] = self.vectors[""][:position]
            self.vectors[""] = expanded
        self.vectors[""][position] = vector
        self.ids[point.id] = position
        self.ids_inv.append(point.id)
        self.payload.append(payload)
        self.deleted = np.append(self.deleted, False)
        self.deleted_per_vector[""] = np.append(self.deleted_per_vector[""], value is None)


class CompactQdrantLocal(QdrantLocal):
    """Native local API with a lock acquired before any archive is opened."""
    def __init__(self, location):
        if importlib.metadata.version("qdrant-client") != "1.14.2":
            raise RuntimeError("Compact archive adapter requires pinned qdrant-client 1.14.2")
        super().__init__(str(location))

    def _load(self):
        if not self.persistent:
            return
        root = Path(self.location)
        root.mkdir(parents=True, exist_ok=True)
        # Preserve the native lock identity/content; never replace/bypass it.
        lock_path = root / ".lock"
        self._flock_file = lock_path.open("a+")
        try:
            portalocker.lock(self._flock_file, portalocker.LockFlags.EXCLUSIVE | portalocker.LockFlags.NON_BLOCKING)
        except portalocker.exceptions.LockException as exc:
            self._flock_file.close()
            raise RuntimeError("Local vector archive is already owned by another process") from exc
        try:
            meta_path = root / META_INFO_FILENAME
            if not meta_path.exists():
                meta_path.write_text(json.dumps({"collections": {}, "aliases": {}}))
            meta = json.loads(meta_path.read_text())
            for name, config_json in meta["collections"].items():
                # Collection paths remain inside the native collection root.
                path = Path(self._collection_path(name))
                if path.resolve().parent != (root / "collection").resolve():
                    raise ValueError("Invalid archive collection path")
                collection = CompactCollection(models.CreateCollection(**config_json), str(path),
                                               self.force_disable_check_same_thread)
                self.collections[name] = collection
            self.aliases = meta["aliases"]
        except Exception:
            for collection in self.collections.values():
                collection.close()
            portalocker.unlock(self._flock_file)
            self._flock_file.close()
            raise

    def reference_records(self, collection_name):
        """Internal read-only views, used only under WorkerState.vector_lock."""
        collection = self._get_collection(collection_name)
        if set(collection.vectors) != {""} or collection.sparse_vectors or collection.multivectors:
            raise ValueError("Unsupported compact index vector configuration")
        for point_id, position in collection.ids.items():
            if not collection.deleted[position] and not collection.deleted_per_vector[""][position]:
                yield str(point_id), collection.vectors[""][position], collection.payload[position]

    def resident_count(self, collection_name):
        return len(self._get_collection(collection_name).ids)

    def existing_point_ids(self, collection_name, ids):
        collection = self._get_collection(collection_name)
        for point_id in ids:
            candidate = int(point_id) if point_id.isdecimal() else point_id
            if candidate in collection.ids:
                yield candidate

    def archive_status(self):
        return {name: {"loadedStorageRows": getattr(collection,"loaded_storage_rows",0),
                       "residentIdSlots": len(collection.ids), "residentRowSlots": len(collection.ids_inv),
                       "duplicateStorageRowsAtLoad": getattr(collection,"duplicate_storage_rows",0),
                       "denseDtype": str(collection.vectors[""].dtype)}
                for name,collection in self.collections.items()}
