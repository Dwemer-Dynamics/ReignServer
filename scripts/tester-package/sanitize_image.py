"""Remove only task-image machine identity and termination logs before auditing."""
import argparse
import copy
import io
from pathlib import Path
import tarfile

def regular_member(original):
    """Copy a member so its current logical payload is written as an ordinary stream."""
    item=copy.copy(original);pax=dict(original.pax_headers)
    layout=[key for key in pax if key.startswith('GNU.sparse.')]
    if layout or original.sparse is not None or original.type==tarfile.GNUTYPE_SPARSE:
        for key in layout:del pax[key]
        if pax.get('path',item.name)!=item.name:del pax['path']
        pax.pop('size',None);item.sparse=None
        if item.type==tarfile.GNUTYPE_SPARSE:item.type=tarfile.REGTYPE
    item.pax_headers=pax
    return item

def sanitize(source, destination):
    if destination.exists() or source.resolve()==destination.resolve():
        raise ValueError('Keep original image and choose a new destination')
    changed=[]
    with tarfile.open(source,'r|*') as src,tarfile.open(destination,'w|',format=tarfile.PAX_FORMAT) as dst:
        for original in src:
            item=regular_member(original)
            name=item.name.removeprefix('./').rstrip('/')
            if name=='var/log/journal' or name.startswith('var/log/journal/'):
                changed.append(name);continue
            if item.isfile() and name.startswith(('var/www/html/ReignServer/data/','var/lib/postgresql/','etc/ssl/private/','etc/reignserver/')):
                raise ValueError('Private state cannot be sanitized into a distributable image')
            if name=='etc/resolv.conf':
                changed.append(name);continue
            if name.startswith('var/www/html/ReignServer/'):
                item.uid=item.gid=0;item.uname=item.gname='root';item.mode &= ~0o022
            if item.isfile() and (name.startswith('var/log/') or name in ('etc/machine-id','var/lib/dbus/machine-id')):
                if item.size:changed.append(name)
                item.size=0;item.pax_headers.pop('size',None)
                dst.addfile(item,io.BytesIO())
            else:
                dst.addfile(item,src.extractfile(original) if original.isfile() else None)
    return changed

if __name__=='__main__':
    p=argparse.ArgumentParser(description=__doc__)
    p.add_argument('--source',required=True,type=Path);p.add_argument('--output',required=True,type=Path)
    a=p.parse_args();print(sanitize(a.source,a.output))
