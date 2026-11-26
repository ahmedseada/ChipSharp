// ChipSharp runtime, shared by every runtime of a target with a heap: objects, their allocator and the garbage
// collector (mark and sweep).
//
// Freestanding: no headers and no C library. The code generator calls the first three (src/ChipSharp.Backend.Llvm,
// LlvmHeap); the others are the runtime functions of ChipSharp.Runtime's GC methods, for System.GC and the tests.
//
//   chipsharp_heap_initialize            sets the heap up, before the program runs: called first by chipsharp_entry of
//                                        a program that makes objects, with the table of its static roots
//   chipsharp_new_object                 a new object of a class: zeroed, its header the address of its type
//   chipsharp_new_array                  a new array: zeroed, its header and its length set
//   ChipSharp_Runtime_GcCollect          collects now (GC.Collect)
//   ChipSharp_Runtime_GcCollectionCount  how many collections ran (GC.CollectionCount)
//   ChipSharp_Runtime_GcTotalMemory      the bytes of the objects not known to be garbage (GC.GetTotalMemory)
//   ChipSharp_Runtime_GcTotalAllocatedBytes  the bytes of every object ever made (GC.GetTotalAllocatedBytes)
//   ChipSharp_Runtime_GcHeapSize         the bytes the heap has for objects
//
// What each runtime gives this file (chipsharp_heap_windows.c on a PC, armv6m_heap.c on an ARMv6-M chip):
//
//   chipsharp_heap_memory                where the heap is: the target's "heap" bytes (on a chip, the linker script's
//                                        heap region, between the variables and the stack)
//   chipsharp_scan_stack                 stores the callee-saved registers on the stack, then calls scan from there to
//                                        the top of the stack
//   chipsharp_heap_lock, _unlock         around a collection: interrupts off and on again, so that no handler changes
//                                        a reference while the collector reads them
//   chipsharp_heap_check_thread          stops the program inside an interrupt handler: handlers never use the heap
//
// and its failures file: ChipSharp_Runtime_OutOfMemory (OutOfMemoryException) and ChipSharp_Runtime_Overflow (an
// array of a negative length: OverflowException).
//
// ---- Objects ------------------------------------------------------------------------------------------------------
//
// An object starts with its header, one word: the address of its type descriptor (chipsharp_type, which the code
// generator writes in read-only data, see TirTypeDescriptor), then its fields. An array is the header, its length (an
// int) and its elements from type->size on. A type says where the references in an object (or in an element of an
// array) are, so the collector follows exactly those: a ptr field, such as fixed text in flash, is never followed, and
// a reference to an object outside the heap (none yet; later constant objects in flash) is never marked.
//
// ---- The heap -----------------------------------------------------------------------------------------------------
//
// The heap memory is, in order: the mark stack (a word per 64 words of heap, from 32 up to MARK_ENTRIES), the
// block-start bitmap (one bit per 8 bytes of blocks) and the blocks, which cover the rest of it, one after another, each a multiple of 8 bytes long and at an
// address that is a multiple of 8:
//
//   an object    its header is its type's address (bit 0 set while a collection has marked it); its size comes from
//                its type (and its length): the object's size rounded up to 8, at least MIN_BLOCK
//   free         its header is its size with bit 1 set (FREE); one of at least two words is on the free list of its
//                size class, the second word linking to the next; a smaller one (8 bytes on a 64-bit PC) is on none
//
// The bitmap has the bit of the first 8 bytes of every block set, and no other, so the block that holds any address
// in the heap is the last block that starts at or before it: that is how a word on the stack that points into an
// object, not only at its start (as optimized code may keep it), keeps the object alive.
//
// Allocation carves objects one after another from the front of one free block (bump .. bump_end), taken from the
// free lists: the first block of the request's size class that is big enough, else the first of a bigger class. At
// the start, the whole heap is one free block. When no free block is big enough, the collector runs, and when there is
// still none, the program stops with .NET's OutOfMemoryException message. Every object is zeroed when it is made.
//
// ---- The collector ------------------------------------------------------------------------------------------------
//
// Mark: from the roots, every object they reach gets its mark bit, and goes on the mark stack until its references
// are read:
//   - the static fields: exactly the references the code generator lists in the table of static roots, pairs of
//     the address of a run of references and how many there are;
//   - the stack, conservatively: every word from the stack pointer (after the callee-saved registers are stored on
//     the stack, so that a reference kept in a register across a call is seen too) to the top of the stack that points
//     into an object keeps it alive;
//   - the objects themselves, precisely, with their types.
// When the mark stack is full, an object is marked but not put on it, and once the stack is empty the heap is walked
// again for marked objects to read their references, until nothing was left out: deep lists and big arrays need no
// more memory than the fixed mark stack.
// Sweep: one walk over the blocks clears the marks of the live objects, and joins every run of garbage and free
// blocks into one free block on its list.
//
// The collector runs only when an allocation does not fit (or GC.Collect asks), with interrupts off, never inside an
// interrupt handler: a handler that makes an object stops the program, saying so.
//
// ---- What it does not do (yet) --------------------------------------------------------------------------------------
//
//   - It never moves an object, so the free memory can be in pieces: an object bigger than every free block is out of
//     memory even when the pieces together would hold it.
//   - The stack is read conservatively: a number that happens to point into an object keeps that garbage alive until
//     the number is gone, and a pointer the compiler keeps only before an object's start (never seen so far) would not
//     keep it alive.
//   - A collection reads the whole heap (sweep) and the live objects (mark) at once, with interrupts off: its time
//     grows with the heap, and interrupts wait for it: on the Pico (125 MHz, 224 KB) about 21 ms for a heap full of
//     small live objects, 5 ms for one full of garbage.
//   - One thread: the program and its interrupt handlers on one core (the Pico's second core is not used).

typedef __UINTPTR_TYPE__ uintptr_t;

// A type descriptor, as the code generator writes it: see TirTypeDescriptor.
typedef struct chipsharp_type chipsharp_type;

typedef struct
{
    const chipsharp_type *interface;
    void *const *methods;
} chipsharp_interface;

struct chipsharp_type
{
    unsigned int size;            // a class: the size of its objects, header included; an array: where element 0 is
    unsigned int element_size;    // an array: the size of one element; 0 for a class
    unsigned int flags;           // TYPE_ARRAY, TYPE_INTERFACE
    unsigned int reference_count; // how many offsets references lists
    const unsigned int *references; // where the references are: in an object, or in one element of an array
    const chipsharp_type *base;   // the base class; for an array of references, the type of its elements
    const char *name;             // the type's name, UTF-8, ending with 0
    const chipsharp_interface *interfaces;
    unsigned int interface_count;
    unsigned int virtual_count;
    void *virtuals[];             // the virtual method table
};

// A run of count references one after another in the static fields: an entry of the table of static roots.
typedef struct
{
    uintptr_t *start;
    uintptr_t count;
} chipsharp_root;

#define TYPE_ARRAY 1u
#define TYPE_INTERFACE 2u

#define WORD ((uintptr_t)sizeof(uintptr_t))
#define GRANULE ((uintptr_t)8)
#define MIN_BLOCK (2 * WORD > GRANULE ? 2 * WORD : GRANULE)
#define MARK ((uintptr_t)1)
#define FREE ((uintptr_t)2)
#define SIZE_BITS (~(GRANULE - 1))

// The size classes of the free lists: class k holds the free blocks of 8 * 2^k bytes up to twice that (the last one
// every bigger block too).
#define CLASSES 24

// The most objects the mark stack holds before the collector walks the heap for the ones left out.
#define MARK_ENTRIES (WORD == 4 ? 64u : 1024u)
#define MARK_ENTRIES_LEAST 32u

// What each runtime gives (see the top of this file).
void chipsharp_heap_memory(char **start, char **end);
void chipsharp_scan_stack(void (*scan)(uintptr_t *from, uintptr_t *to));
unsigned int chipsharp_heap_lock(void);
void chipsharp_heap_unlock(unsigned int state);
void chipsharp_heap_check_thread(void);
__attribute__((noreturn)) void ChipSharp_Runtime_OutOfMemory(void);
__attribute__((noreturn)) void ChipSharp_Runtime_Overflow(void);

typedef struct free_block
{
    uintptr_t header; // the block's size | FREE
    struct free_block *next;
} free_block;

static char *heap_start; // the first block
static char *heap_end;   // where the blocks end
static unsigned int *starts; // the block-start bitmap: bit i of word w is the 8 bytes at heap_start + (32 w + i) * 8
static char **mark_stack;
static unsigned int mark_entries;
static unsigned int mark_depth;
static int mark_overflow;
static free_block *lists[CLASSES];
static char *bump;     // the free block objects are carved from: its front
static char *bump_end; // and its end
static const chipsharp_root *static_roots;
static uintptr_t static_root_count;

static uintptr_t used_bytes;               // in objects that no collection has found to be garbage
static unsigned long long allocated_bytes; // in every object ever made
static unsigned int collections;

// ---- Blocks --------------------------------------------------------------------------------------------------------

// The number of the highest bit set in value, which is not 0.
static unsigned int highest_bit(uintptr_t value)
{
    unsigned int bit = 0;
    for (unsigned int step = WORD * 4; step > 0; step /= 2)
    {
        if (value >> step)
        {
            value >>= step;
            bit += step;
        }
    }

    return bit;
}

static unsigned int size_class(uintptr_t size)
{
    unsigned int k = highest_bit(size / GRANULE);
    return k < CLASSES ? k : CLASSES - 1;
}

static void set_start(char *block)
{
    uintptr_t granule = (uintptr_t)(block - heap_start) / GRANULE;
    starts[granule / 32] |= 1u << (granule % 32);
}

static void clear_start(char *block)
{
    uintptr_t granule = (uintptr_t)(block - heap_start) / GRANULE;
    starts[granule / 32] &= ~(1u << (granule % 32));
}

// The block that holds address, an address in the heap: the last one that starts at or before it.
static char *block_at(char *address)
{
    uintptr_t granule = (uintptr_t)(address - heap_start) / GRANULE;
    uintptr_t word = granule / 32;
    unsigned int bits = starts[word] & (0xffffffffu >> (31 - granule % 32));
    while (bits == 0)
    {
        if (word == 0)
        {
            return 0;
        }

        bits = starts[--word];
    }

    return heap_start + (word * 32 + highest_bit(bits)) * GRANULE;
}

static const chipsharp_type *type_of(char *object)
{
    return (const chipsharp_type *)(*(uintptr_t *)object & ~MARK);
}

// The block size of an object of type with length elements (an array) or none: a multiple of 8, at least MIN_BLOCK.
static uintptr_t object_size(const chipsharp_type *type, unsigned int length)
{
    uintptr_t size = type->size + (uintptr_t)type->element_size * length;
    size = (size + GRANULE - 1) & SIZE_BITS;
    return size < MIN_BLOCK ? MIN_BLOCK : size;
}

static uintptr_t block_size(char *block)
{
    uintptr_t header = *(uintptr_t *)block;
    if (header & FREE)
    {
        return header & SIZE_BITS;
    }

    const chipsharp_type *type = type_of(block);
    return object_size(type, (type->flags & TYPE_ARRAY) ? *(unsigned int *)(block + WORD) : 0);
}

// Makes [block, block + size) one free block (its start bit is set already) and puts it on its list.
static void make_free(char *block, uintptr_t size)
{
    free_block *free = (free_block *)block;
    free->header = size | FREE;
    if (size >= 2 * WORD)
    {
        unsigned int k = size_class(size);
        free->next = lists[k];
        lists[k] = free;
    }
}

// The rest of the free block objects are carved from becomes a free block of its own.
static void seal(void)
{
    if (bump < bump_end)
    {
        set_start(bump);
        make_free(bump, (uintptr_t)(bump_end - bump));
    }

    bump = 0;
    bump_end = 0;
}

// Takes a free block of at least size bytes off its list to carve objects from; 0 when there is none.
static int refill(uintptr_t size)
{
    seal();
    for (unsigned int k = size_class(size); k < CLASSES; k++)
    {
        free_block **link = &lists[k];
        for (free_block *block = *link; block != 0; link = &block->next, block = *link)
        {
            uintptr_t free_size = block->header & SIZE_BITS;
            if (free_size >= size)
            {
                *link = block->next;
                bump = (char *)block;
                bump_end = bump + free_size;
                return 1;
            }
        }
    }

    return 0;
}

static char *carve(uintptr_t size)
{
    if ((uintptr_t)(bump_end - bump) < size)
    {
        return 0;
    }

    char *block = bump;
    bump += size;
    return block;
}

// ---- The collector -------------------------------------------------------------------------------------------------

// Marks the object, which is not marked or free, and puts it on the mark stack when it has references to read.
static void mark(char *object)
{
    uintptr_t header = *(uintptr_t *)object;
    if (header & MARK)
    {
        return;
    }

    *(uintptr_t *)object = header | MARK;
    if (((const chipsharp_type *)header)->reference_count == 0)
    {
        return;
    }

    if (mark_depth < mark_entries)
    {
        mark_stack[mark_depth++] = object;
    }
    else
    {
        mark_overflow = 1; // the walk over the heap will read its references
    }
}

// A reference from a field or a static root: null, an object in the heap, or an object outside it (never marked).
static void mark_reference(uintptr_t reference)
{
    if (reference - (uintptr_t)heap_start < (uintptr_t)(heap_end - heap_start))
    {
        mark((char *)reference);
    }
}

// A word from the stack, which may be anything: it keeps alive the object it points into, if any.
static void mark_maybe(uintptr_t word)
{
    if (word - (uintptr_t)heap_start >= (uintptr_t)(heap_end - heap_start))
    {
        return;
    }

    char *block = block_at((char *)word);
    if (block == 0 || (*(uintptr_t *)block & FREE) || word >= (uintptr_t)block + block_size(block))
    {
        return;
    }

    mark(block);
}

static void scan_conservatively(uintptr_t *from, uintptr_t *to)
{
    for (uintptr_t *word = from; word < to; word++)
    {
        mark_maybe(*word);
    }
}

// Marks what the object's references refer to.
static void scan_object(char *object)
{
    const chipsharp_type *type = type_of(object);
    const unsigned int *references = type->references;
    unsigned int count = type->reference_count;
    if (type->flags & TYPE_ARRAY)
    {
        unsigned int length = *(unsigned int *)(object + WORD);
        char *element = object + type->size;
        for (unsigned int i = 0; i < length; i++, element += type->element_size)
        {
            for (unsigned int r = 0; r < count; r++)
            {
                mark_reference(*(uintptr_t *)(element + references[r]));
            }
        }
    }
    else
    {
        for (unsigned int r = 0; r < count; r++)
        {
            mark_reference(*(uintptr_t *)(object + references[r]));
        }
    }
}

static void drain(void)
{
    while (mark_depth > 0)
    {
        scan_object(mark_stack[--mark_depth]);
    }
}

// After the mark stack was full: reads the references of every marked object, until nothing more was left out.
static void rescan(void)
{
    while (mark_overflow)
    {
        mark_overflow = 0;
        for (char *block = heap_start; block < heap_end; block += block_size(block))
        {
            uintptr_t header = *(uintptr_t *)block;
            if (!(header & FREE) && (header & MARK) && type_of(block)->reference_count != 0)
            {
                scan_object(block);
                drain();
            }
        }
    }
}

// Clears the marks of the live objects, and makes each run of garbage and free blocks one free block.
static void sweep(void)
{
    for (unsigned int k = 0; k < CLASSES; k++)
    {
        lists[k] = 0;
    }

    used_bytes = 0;
    char *run = 0;
    for (char *block = heap_start; block < heap_end;)
    {
        uintptr_t header = *(uintptr_t *)block;
        uintptr_t size = block_size(block);
        if (!(header & FREE) && (header & MARK))
        {
            *(uintptr_t *)block = header & ~MARK;
            used_bytes += size;
            if (run != 0)
            {
                make_free(run, (uintptr_t)(block - run));
                run = 0;
            }
        }
        else if (run == 0)
        {
            run = block;
        }
        else
        {
            clear_start(block);
        }

        block += size;
    }

    if (run != 0)
    {
        make_free(run, (uintptr_t)(heap_end - run));
    }
}

static void collect(void)
{
    unsigned int state = chipsharp_heap_lock();
    seal();
    mark_depth = 0;
    mark_overflow = 0;
    for (uintptr_t r = 0; r < static_root_count; r++)
    {
        for (uintptr_t i = 0; i < static_roots[r].count; i++)
        {
            mark_reference(static_roots[r].start[i]);
        }
    }

    chipsharp_scan_stack(scan_conservatively);
    drain();
    rescan();
    sweep();
    collections++;
    chipsharp_heap_unlock(state);
}

// ---- Allocation ----------------------------------------------------------------------------------------------------

// A zeroed block of size bytes (a multiple of 8) whose header is type's address.
static char *allocate(uintptr_t size, const chipsharp_type *type)
{
    chipsharp_heap_check_thread();
    char *block = carve(size);
    if (block == 0 && refill(size))
    {
        block = carve(size);
    }

    if (block == 0)
    {
        collect();
        if (refill(size))
        {
            block = carve(size);
        }
    }

    if (block == 0)
    {
        ChipSharp_Runtime_OutOfMemory();
    }

    set_start(block);
    for (uintptr_t *word = (uintptr_t *)block, *end = (uintptr_t *)(block + size); word < end; word++)
    {
        *word = 0;
    }

    *(uintptr_t *)block = (uintptr_t)type;
    used_bytes += size;
    allocated_bytes += size;
    return block;
}

void *chipsharp_new_object(const chipsharp_type *type)
{
    return allocate(object_size(type, 0), type);
}

void *chipsharp_new_array(const chipsharp_type *type, int length)
{
    if (length < 0)
    {
        ChipSharp_Runtime_Overflow();
    }

    // more elements than fit in the whole heap would overflow the size, so they are out of memory at once
    if (type->element_size != 0 && (uintptr_t)length > (uintptr_t)(heap_end - heap_start) / type->element_size)
    {
        chipsharp_heap_check_thread();
        ChipSharp_Runtime_OutOfMemory();
    }

    char *array = allocate(object_size(type, (unsigned int)length), type);
    *(unsigned int *)(array + WORD) = (unsigned int)length;
    return array;
}

void chipsharp_heap_initialize(const chipsharp_root *roots, uintptr_t count)
{
    static_roots = roots;
    static_root_count = count;

    char *start;
    char *end;
    chipsharp_heap_memory(&start, &end);
    start = (char *)(((uintptr_t)start + GRANULE - 1) & SIZE_BITS);
    end = (char *)((uintptr_t)end & SIZE_BITS);
    uintptr_t total = end > start ? (uintptr_t)(end - start) : 0;
    uintptr_t entries = total / (64 * WORD);
    mark_entries = entries > MARK_ENTRIES ? MARK_ENTRIES
        : entries < MARK_ENTRIES_LEAST ? MARK_ENTRIES_LEAST
        : (unsigned int)entries;
    uintptr_t marks = mark_entries * WORD;
    if (total < marks + 8 * 32 * 2)
    {
        heap_start = heap_end = start; // no room for any object: every allocation is out of memory
        return;
    }

    // each 256 bytes of blocks take one 32-bit word of the bitmap
    mark_stack = (char **)start;
    uintptr_t blocks = (uintptr_t)(end - start - marks) / (8 * 32 + 4) * (8 * 32);
    uintptr_t bitmap = ((blocks / (8 * 32) * 4) + GRANULE - 1) & SIZE_BITS;
    starts = (unsigned int *)(start + marks);
    for (uintptr_t i = 0; i < bitmap / 4; i++)
    {
        starts[i] = 0;
    }

    heap_start = start + marks + bitmap;
    heap_end = heap_start + blocks;
    if (heap_end > end)
    {
        heap_end -= 8 * 32; // the bitmap's rounding up took a little of the last 256 bytes
    }

    set_start(heap_start);
    make_free(heap_start, (uintptr_t)(heap_end - heap_start));
}

// ---- ChipSharp.Runtime's GC methods ---------------------------------------------------------------------------------

void ChipSharp_Runtime_GcCollect(void)
{
    chipsharp_heap_check_thread();
    if (heap_end != heap_start)
    {
        collect();
    }
}

int ChipSharp_Runtime_GcCollectionCount(void)
{
    return (int)collections;
}

long long ChipSharp_Runtime_GcTotalMemory(void)
{
    return (long long)used_bytes;
}

long long ChipSharp_Runtime_GcTotalAllocatedBytes(void)
{
    return (long long)allocated_bytes;
}

long long ChipSharp_Runtime_GcHeapSize(void)
{
    return (long long)(heap_end - heap_start);
}
