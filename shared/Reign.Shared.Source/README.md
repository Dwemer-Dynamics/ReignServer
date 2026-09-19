# Shared source ownership

ReignServer owns these dependency-free helpers. Both the server and client compile the same files, retaining their existing namespaces and assembly identities. Edit the files here once. Do not copy them back into the private client's source tree.

This is deliberately a source package, not another runtime assembly: existing serialized names and Bannerlord save compatibility remain unchanged. The four `Core/Reign*Core.cs` helpers and the native portrait generator's shared appearance helper follow the same ownership rule. `ReignServerRoot` in the client project selects either the paired checkout or the integration workspace; releases record the exact server source revision.
