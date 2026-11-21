# Immutable slot preparation

Preparation validates a private payload before publishing its immutable directory.
Concurrent callers validate the winning slot and return the same receipt; none
activates it, overwrites it, or touches the pointer or legacy current directory.
Archives permit at most 10,000 entries, 512 MiB per entry and 1 GiB expanded total;
streamed bytes must match declared lengths. Device names and short-name aliases
are rejected. Reuse rejects unexpected files and directories, including empty ones.

Known limit: reparse checks are point-in-time checks, not directory-handle pinning
across validation/publication. Callers must exclude concurrent untrusted filesystem
mutators from the installation ancestry; preparation does not prove atomic path resolution.
