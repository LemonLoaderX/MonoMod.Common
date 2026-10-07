# Agent Note: Initialize fresh ARM64 native exception slots

Status: implemented

## Problem

The POSIX ARM64 helper allocates eight bytes for a thread's exception pointer but
does not initialize them. The managed compiler callback reads the slot before
deciding whether compilation produced a native exception. Reused malloc contents
can therefore masquerade as an unwind exception on a fresh thread.

## Decision

The helper stores a zero pointer immediately after allocating a new slot, before
publishing it through pthread_setspecific. Retrieving an existing slot leaves its
contents unchanged, preserving pending exceptions across nested transitions.
The consuming MonoMod repository tests the native helper on fresh threads after
dirtying and freeing same-size allocations, then checks same-thread state retention.

## Alternatives considered

- Clearing every retrieval avoids stale bytes but destroys pending exceptions
  that nested managed/native transitions must preserve.
- Clearing only the managed callback's local view is smaller at the call site,
  but other helper entry points still observe uninitialized storage. Initialization
  belongs to the producer of the per-thread state.

## Consequences

One store is added per thread's first slot allocation; retrieval behavior and
the ABI remain unchanged. The fixture catches the dirty-allocation case without
depending on a game crash. This proves a slot-initialization defect, not causality
for every GC fault or translated-runtime stall. TLS-key creation, allocation
failure and general JIT hook behavior retain their separate failure boundaries.

## Prior-note Audit

No active agent notes exist in this source fork. PATCHES.md owns the native
exception propagation contract and remains authoritative for the upstream base
and adaptation removal criteria.
