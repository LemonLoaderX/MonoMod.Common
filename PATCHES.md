# CoreCLR compatibility patch

This branch extends upstream commit
`d679ae74d002e513bd88e52091b66283d8537d83` for .NET 10 CoreCLR hosts.

`DynamicMethod` stores its return type in the private `_returnType` field on
.NET 10. `DMDEmitDynamicMethodGenerator` now checks that name after the existing
runtime-specific names. The fallback preserves behavior on older runtimes and
avoids downstream assembly rewriting.

Validate the patch by generating a `DynamicMethodDefinition` on .NET 10 and by
running the consuming Harmony dynamic-delegate probe.
