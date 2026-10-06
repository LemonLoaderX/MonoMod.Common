#include <stdint.h>
#include <stddef.h>
#include "jit-info-layout.h"

// Exact interface layouts from runtime b85b9fbd264f, corjit.h.
struct chunk {
    uint32_t alignment, size, flags;
    void *block, *block_rw;
};
struct allocation {
    struct chunk *chunks;
    uint32_t count, exception_count;
};
struct wrapper {
    void **vtable;
    void *original;
    void *code, *code_rw;
    uintptr_t code_size;
};
_Static_assert(sizeof(struct chunk) == 32, "Unexpected ARM64 allocation layout");
_Static_assert(offsetof(struct wrapper, original) == 8, "Unexpected proxy layout");

void mm_jit_alloc_mem(struct wrapper *self, struct allocation *args) {
    typedef void (*alloc_mem)(void *, struct allocation *);
    ((alloc_mem)(*(void ***)self->original)[MM_JIT_ALLOC_MEM_SLOT])(self->original, args);
    for (uint32_t index = 0; index < args->count; index++) {
        struct chunk *chunk = &args->chunks[index];
        if ((chunk->flags & 1) != 0) {
            self->code = chunk->block;
            self->code_rw = chunk->block_rw;
            self->code_size = chunk->size;
            break;
        }
    }
}
