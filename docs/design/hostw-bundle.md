# Closed hostw bundles

C2 requires the package-bound digest, native PE architecture/static-runtime exports, apphost marker and .NET 10 v6 manifest.
All embedded ranges, lengths, decompression, bundle identity, JSON locations, self-contained runtime config and dependency closure
must agree. Missing sidecars alone never establish completeness. No code executes.
The embedded SDK inventory is authoritative: statically linked native members are not inferred from pre-bundle sidecars.

Sources: [manifest](https://github.com/dotnet/runtime/blob/v10.0.0/src/installer/managed/Microsoft.NET.HostModel/Bundle/Manifest.cs), [static runtime linkage](https://github.com/dotnet/runtime/blob/v10.0.0/src/native/corehost/apphost/static/CMakeLists.txt), [native exports](https://github.com/dotnet/runtime/blob/v10.0.0/src/native/corehost/apphost/static/singlefilehost.def).
Fail closed on other formats/flags; limits are 10,000 entries, 512 MiB per file,
1 GiB expanded total and 4 MiB per embedded JSON document.
Exports require mapped, non-forwarded function targets and valid ordinal tables.
Compressed input must terminate at BFINAL with no trailing bytes; dependency graphs
must be acyclic and JSON property names unique recursively.
The 16 MiB native-section limit remains fail-closed. Unknown entries are hash-bound
data, never substitutes for typed runtime assets. Manifest hashing follows explicit record order.
