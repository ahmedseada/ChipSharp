// ChipSharp runtime, shared by every runtime of a target with a heap: objects, their allocator and the garbage
// collector (mark and sweep).
//
// Freestanding: no headers and no C library. The code generator calls the first three (src/ChipSharp.Backend.Llvm,
// LlvmHeap); the others are the runtime functions of System.GC's methods (KnownExternals) and of the tests.
//
//   chipsharp_heap_initialize            sets the heap up, before the program runs: called first by chipsharp_entry of
//                                        a program that makes objects, with the table of its static roots
//   chipsharp_new_object                 a new object of a class: zeroed, its header the address of its type
//   chipsharp_new_array                  a new array: zeroed, its header and its length set
//   ChipSharp_Runtime_GcCollect          collects now (GC.Collect())
//   ChipSharp_Runtime_GcGetTotalMemory   the bytes of the objects not known to be garbage, after a collection when asked
//                                        (GC.GetTotalMemory(bool))
//   ChipSharp_Runtime_GcKeepAlive        nothing: a call the object is passed to, so it is alive until there
//                                        (GC.KeepAlive(object))
//   ChipSharp_Runtime_GcCollectionCount  how many collections ran
//   ChipSharp_Runtime_GcTotalMemory      the bytes of the objects not known to be garbage
//   ChipSharp_Runtime_GcTotalAllocatedBytes  the bytes of every object ever made
//   ChipSharp_Runtime_GcHeapSize         the bytes the heap has for objects
//
// What each runtime gives this file (chipsharp_heap_windows.c on a PC, armv6m_heap.c on an ARMv6-M chip):
//
//   chipsharp_heap_memory                where the heap is: the target's "heap" bytes (on a chip, the linker script's
//                                        heap region, between the variables and the stack)
//   chipsharp_scan_stack                 stores the callee-saved registers on the stack, then calls scan from there to
//                                        the top of the stack
//   chipsharp_heap_check_thread          stops the program inside an interrupt handler: handlers never use the heap
//
// and its failures file: ChipSharp_Runtime_OutOfMemory (OutOfMemoryException), ChipSharp_Runtime_Overflow (an array
// of a negative length: OverflowException) and, built with chipsharp build --verify-heap, chipsharp_fatal (the
// heap is corrupted).
//
// ---- Objects ------------------------------------------------------------------------------------------------------
//
// An object starts with its header, one word: the address of its type descriptor (chipsharp_type, which the code
// generator writes in read-only data, see TirTypeDescriptor), then its fields. An array is the header, its length (an
// int) and its elements from type->size on. A type says where the references in an object (or in an element of an
// array) are, so the collector follows exactly those: a ptr field, such as fixed text in flash, is never followed, and
// a reference to an object outside the heap (fixed text, the empty arrays) is never marked. The collector never writes
// an object's header: it keeps its marks in a bitmap of its own, so that an interrupt handler that comes during a
// collection reads every object as it is (its virtual methods, its type, a string's chars).
//
// ---- The heap -----------------------------------------------------------------------------------------------------
//
// The heap memory is, in order: the mark stack (a word per 64 words of heap, from 32 up to MARK_ENTRIES), the
// block-start bitmap and the mark bitmap (one bit per 8 bytes of blocks each, 1/32 of the heap together) and the
// blocks, which cover the rest of it, one after another, each a multiple of 8 bytes long and at an address that is a
// multiple of 8:
//
//   an object    its header is its type's address; its size comes from its type (and its length): the object's size
//                rounded up to 8, at least MIN_BLOCK
//   free         its header is its size with bit 1 set (FREE); one of at least two words is on the free list of its
//                size class, the second word linking to the next; a smaller one (8 bytes on a 64-bit PC) is on none
//
// The start bitmap has the bit of the first 8 bytes of every block set, and no other, so the block that holds any
// address in the heap is the last block that starts at or before it: that is how a word on the stack that points into
// an object, not only at its start (as optimized code may keep it), keeps the object alive. The mark bitmap has the bit
// of an object's first 8 bytes set while a collection has marked it.
//
// Allocation carves small objects one after another from the front of one free block (bump .. bump_end), taken from
// the free lists: the first block of the request's size class that is big enough, else the first of a bigger class,
// so that the holes small objects leave are filled by small objects again. A big object (LARGE bytes or more: arrays,
// long strings) is not carved from that block: it takes the smallest free block that holds it from the first size
// class that has one (best fit), whose rest goes back on its list, so that big blocks are not used up by small objects
// and the small objects' block is not cut by big ones. At the start, the whole heap is one free block. When no free
// block is big enough, the collector runs, and when there is still none, the program stops with .NET's
// OutOfMemoryException message. Every object is zeroed when it is made.
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
// blocks into one free block on its list: two free blocks are never next to each other after a collection.
//
// The collector runs only when an allocation does not fit (or GC.Collect asks), never inside an interrupt handler (a
// handler that makes an object stops the program, saying so), and with interrupts on: an interrupt that comes during
// a collection is handled at once, as at any other time. That is sound because a handler changes nothing the collector
// reads: it makes no object (the whole-program pass rejects it, and the runtime stops it), it stores no reference
// other than null in a static field, an object or an array (the whole-program pass rejects that too: a reference it
// moved from where the collector has not looked yet to where it has looked already would not be marked), and the
// collector never writes what a handler can reach: live objects keep their headers, and only garbage, which no
// handler can reach, becomes free blocks. What GC.GetTotalMemory reads changes once, at the end of the sweep.
//
// Less garbage kept alive by the conservative scan: before it collects, the collector zeroes the stack its own frames
// are about to take (CLEARED_WORDS words below where it is called), so that what earlier calls left there, deeper than
// the program is now, keeps nothing alive; and the code generator starts every slot of a function at zero.
//
// Built with chipsharp build --verify-heap (CHIPSHARP_VERIFY_HEAP), every collection starts and ends with a check of the
// whole heap (what the program did to it, then what the collector did): that the blocks cover it exactly and each has
// its start bit, that every object's header is a type descriptor and every reference in it null, outside the heap or the
// start of an object, that no mark is left, that the bytes in use add up, and that each free list holds free blocks of
// its size; else the program stops with what is wrong and where.
//
// ---- What it does not do (yet) --------------------------------------------------------------------------------------
//
//   - It never moves an object, so the free memory can be in pieces: an object bigger than every free block is out of
//     memory even when the pieces together would hold it.
//   - The stack is read conservatively: a number that happens to point into an object keeps that garbage alive until
//     the number is gone (also a reference the program no longer uses that a register or a slot of a frame still
//     holds), and a pointer the compiler keeps only before an object's start (never seen so far) would not keep it
//     alive.
//   - A collection reads the whole heap (sweep) and the live objects (mark) at once: its time grows with the heap (on
//     the Pico, 125 MHz and 224 KB, about 20 ms for a heap full of small live objects, 5 ms for one full of garbage),
//     and the program waits for it, but interrupt handlers do not.
//   - One thread: the program and its interrupt handlers on one core (the Pico's second core is not used).

typedef __UINTPTR_TYPE__ uintptr_t;

// A C# int and uint: 32 bits whatever the compiler's int (16 on an 8-bit chip), as the code generators pass an array's
// length and lay it out in the array.
typedef __INT32_TYPE__ s32;
typedef __UINT32_TYPE__ u32;

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
#define FREE ((uintptr_t)2)
#define SIZE_BITS (~(GRANULE - 1))

// The size classes of the free lists: class k holds the free blocks of 8 * 2^k bytes up to twice that (the last one
// every bigger block too).
#define CLASSES 24

// The size from which an object takes a free block of its own, the best fit, instead of being carved from the block
// small objects are carved from.
#define LARGE ((uintptr_t)256)

// The most objects the mark stack holds before the collector walks the heap for the ones left out.
#define MARK_ENTRIES (WORD == 4 ? 64u : 1024u)
#define MARK_ENTRIES_LEAST 32u

// The words of stack below where the collector is called that it zeroes first: more than its own frames take.
#define CLEARED_WORDS 64u

// What each runtime gives (see the top of this file).
void chipsharp_heap_memory(char **start, char **end);
void chipsharp_scan_stack(void (*scan)(uintptr_t *from, uintptr_t *to));
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
static unsigned int *marks;  // the mark bitmap, laid out the same way
static char **mark_stack;
static unsigned int mark_entries;
static unsigned int mark_depth;
static int mark_overflow;
static free_block *lists[CLASSES];
static char *bump;     // the free block small objects are carved from: its front
static char *bump_end; // and its end
static const chipsharp_root *static_roots;
static uintptr_t static_root_count;

static volatile uintptr_t used_bytes;      // in objects that no collection has found to be garbage
static unsigned long long allocated_bytes; // in every object ever made
static volatile unsigned int collections;

#ifdef CHIPSHARP_VERIFY_HEAP
static void verify(const char *when, unsigned int collection);
#endif

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

// The granule of address in the heap, as the bitmaps count them.
static uintptr_t granule_of(const char *address)
{
    return (uintptr_t)(address - heap_start) / GRANULE;
}

static void set_bit(unsigned int *bitmap, const char *block)
{
    uintptr_t granule = granule_of(block);
    bitmap[granule / 32] |= 1u << (granule % 32);
}

static void clear_bit(unsigned int *bitmap, const char *block)
{
    uintptr_t granule = granule_of(block);
    bitmap[granule / 32] &= ~(1u << (granule % 32));
}

static int has_bit(const unsigned int *bitmap, const char *block)
{
    uintptr_t granule = granule_of(block);
    return (bitmap[granule / 32] >> (granule % 32)) & 1u;
}

// The block that holds address, an address in the heap: the last one that starts at or before it.
static char *block_at(char *address)
{
    uintptr_t granule = granule_of(address);
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
    return *(const chipsharp_type **)object;
}

// The block size of an object of type with length elements (an array) or none: a multiple of 8, at least MIN_BLOCK. An
// array without elements takes 8 bytes more, so that the address of its elements, which is what the program keeps of an
// array (see HeapArrays in src/ChipSharp.Frontend), is inside it as for any other array.
static uintptr_t object_size(const chipsharp_type *type, unsigned int length)
{
    uintptr_t size = type->size + (uintptr_t)type->element_size * length;
    if ((type->flags & TYPE_ARRAY) && size == type->size)
    {
        size += GRANULE;
    }

    size = (size + GRANULE - 1) & SIZE_BITS;
    return size < MIN_BLOCK ? MIN_BLOCK : size;
}

static unsigned int length_of(char *array)
{
    return (unsigned int)*(u32 *)(array + WORD);
}

static uintptr_t block_size(char *block)
{
    uintptr_t header = *(uintptr_t *)block;
    if (header & FREE)
    {
        return header & SIZE_BITS;
    }

    const chipsharp_type *type = type_of(block);
    return object_size(type, (type->flags & TYPE_ARRAY) ? length_of(block) : 0);
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

// The rest of the free block small objects are carved from becomes a free block of its own.
static void seal(void)
{
    if (bump < bump_end)
    {
        set_bit(starts, bump);
        make_free(bump, (uintptr_t)(bump_end - bump));
    }

    bump = 0;
    bump_end = 0;
}

// Takes a free block of at least size bytes off its list to carve small objects from; 0 when there is none.
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

// A block of size bytes for a big object: the smallest free block that holds it, of the first size class that has one,
// its rest a free block of its own; 0 when there is none.
static char *take_best(uintptr_t size)
{
    for (unsigned int k = size_class(size); k < CLASSES; k++)
    {
        free_block **best = 0;
        uintptr_t best_size = 0;
        for (free_block **link = &lists[k]; *link != 0; link = &(*link)->next)
        {
            uintptr_t free_size = (*link)->header & SIZE_BITS;
            if (free_size >= size && (best == 0 || free_size < best_size))
            {
                best = link;
                best_size = free_size;
                if (free_size == size)
                {
                    break;
                }
            }
        }

        if (best != 0)
        {
            char *block = (char *)*best;
            *best = (*best)->next;
            if (best_size > size)
            {
                set_bit(starts, block + size);
                make_free(block + size, best_size - size);
            }

            return block;
        }
    }

    return 0;
}

// ---- The collector -------------------------------------------------------------------------------------------------

// Marks the object, which is not free, and puts it on the mark stack when it has references to read.
static void mark(char *object)
{
    if (has_bit(marks, object))
    {
        return;
    }

    set_bit(marks, object);
    if (type_of(object)->reference_count == 0)
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
        unsigned int length = length_of(object);
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
            if (!(*(uintptr_t *)block & FREE) && has_bit(marks, block) && type_of(block)->reference_count != 0)
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

    uintptr_t used = 0;
    char *run = 0;
    for (char *block = heap_start; block < heap_end;)
    {
        uintptr_t size = block_size(block);
        if (!(*(uintptr_t *)block & FREE) && has_bit(marks, block))
        {
            clear_bit(marks, block);
            used += size;
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
            clear_bit(starts, block);
        }

        block += size;
    }

    if (run != 0)
    {
        make_free(run, (uintptr_t)(heap_end - run));
    }

    used_bytes = used;
}

// Zeroes the stack the collector's frames are about to take, below its caller's.
__attribute__((noinline)) static void clear_stack(void)
{
    volatile uintptr_t area[CLEARED_WORDS];
    for (unsigned int i = 0; i < CLEARED_WORDS; i++)
    {
        area[i] = 0;
    }
}

__attribute__((noinline)) static void collect(void)
{
    seal();
#ifdef CHIPSHARP_VERIFY_HEAP
    verify("before collection ", collections + 1);
#endif
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
    collections = collections + 1;
#ifdef CHIPSHARP_VERIFY_HEAP
    verify("after collection ", collections);
#endif
}

// ---- Allocation ----------------------------------------------------------------------------------------------------

// A block of size bytes (a multiple of 8) from the free memory there is now; 0 when it has none that big.
static char *find(uintptr_t size)
{
    if (size >= LARGE)
    {
        return take_best(size);
    }

    char *block = carve(size);
    if (block == 0 && refill(size))
    {
        block = carve(size);
    }

    return block;
}

// A zeroed block of size bytes (a multiple of 8) whose header is type's address.
static char *allocate(uintptr_t size, const chipsharp_type *type)
{
    chipsharp_heap_check_thread();
    char *block = find(size);
    if (block == 0)
    {
        clear_stack();
        collect();
        block = find(size);
    }

    if (block == 0)
    {
        ChipSharp_Runtime_OutOfMemory();
    }

    set_bit(starts, block);
    for (uintptr_t *word = (uintptr_t *)block, *end = (uintptr_t *)(block + size); word < end; word++)
    {
        *word = 0;
    }

    *(uintptr_t *)block = (uintptr_t)type;
    used_bytes = used_bytes + size;
    allocated_bytes += size;
    return block;
}

void *chipsharp_new_object(const chipsharp_type *type)
{
    return allocate(object_size(type, 0), type);
}

void *chipsharp_new_array(const chipsharp_type *type, s32 length)
{
    if (length < 0)
    {
        ChipSharp_Runtime_Overflow();
    }

    // more elements than fit in the whole heap would overflow the size, so they are out of memory at once (compared as
    // 32 bits: a length need not fit in an address)
    if (type->element_size != 0 && (u32)length > (u32)((uintptr_t)(heap_end - heap_start) / type->element_size))
    {
        chipsharp_heap_check_thread();
        ChipSharp_Runtime_OutOfMemory();
    }

    char *array = allocate(object_size(type, (unsigned int)length), type);
    *(u32 *)(array + WORD) = (u32)length;
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
    uintptr_t stack = mark_entries * WORD;
    if (total < stack + 8 * 32 * 2)
    {
        heap_start = heap_end = start; // no room for any object: every allocation is out of memory
        return;
    }

    // each 256 bytes of blocks take one 32-bit word of each bitmap
    mark_stack = (char **)start;
    uintptr_t blocks = (uintptr_t)(end - start - stack) / (8 * 32 + 8) * (8 * 32);
    uintptr_t bitmap = ((blocks / (8 * 32) * 4) + GRANULE - 1) & SIZE_BITS;
    starts = (unsigned int *)(start + stack);
    marks = (unsigned int *)(start + stack + bitmap);
    for (uintptr_t i = 0; i < bitmap / 4; i++)
    {
        starts[i] = 0;
        marks[i] = 0;
    }

    heap_start = start + stack + 2 * bitmap;
    heap_end = heap_start + blocks;
    if (heap_end > end)
    {
        heap_end -= 8 * 32; // the bitmaps' rounding up took a little of the last 256 bytes
    }

    set_bit(starts, heap_start);
    make_free(heap_start, (uintptr_t)(heap_end - heap_start));
}

// ---- System.GC's methods, and the tests' ----------------------------------------------------------------------------

void ChipSharp_Runtime_GcCollect(void)
{
    chipsharp_heap_check_thread();
    if (heap_end != heap_start)
    {
        clear_stack();
        collect();
    }
}

// GC.GetTotalMemory(forceFullCollection): the bytes of the objects not known to be garbage (the size of their blocks:
// each object rounded up to 8 bytes), after a collection when forceFullCollection is true. Every collection is a full
// one, so one is enough.
long long ChipSharp_Runtime_GcGetTotalMemory(int forceFullCollection)
{
    if (forceFullCollection)
    {
        ChipSharp_Runtime_GcCollect();
    }

    return (long long)used_bytes;
}

// GC.KeepAlive(object): nothing, but the object is passed to it, so the program holds it until this call.
void ChipSharp_Runtime_GcKeepAlive(void *object)
{
    __asm__ volatile("" : : "r"(object) : "memory");
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

// ---- The heap verifier (chipsharp build --verify-heap) ------------------------------------------------------------

#ifdef CHIPSHARP_VERIFY_HEAP

// In the failures file: writes the message on its line and stops the program, as .NET stops on a fatal error.
__attribute__((noreturn)) void chipsharp_fatal(const char *message);

// Which check is running: "before collection " or "after collection ", and the number of that collection.
static const char *verify_when;
static unsigned int verify_collection;

// Stops the program: "Fatal error. The heap is corrupted: <what> at 0x... (found before collection <n>)."
__attribute__((noreturn)) static void corrupted(const char *what, const void *where)
{
    static char text[256];
    unsigned int n = 0;
    for (const char *c = "Fatal error. The heap is corrupted: "; *c != 0; c++)
    {
        text[n++] = *c;
    }

    for (const char *c = what; *c != 0 && n < 120; c++)
    {
        text[n++] = *c;
    }

    for (const char *c = " at 0x"; *c != 0; c++)
    {
        text[n++] = *c;
    }

    for (int shift = (int)WORD * 8 - 4; shift >= 0; shift -= 4)
    {
        text[n++] = "0123456789abcdef"[((uintptr_t)where >> shift) & 15];
    }

    for (const char *c = " (found "; *c != 0; c++)
    {
        text[n++] = *c;
    }

    for (const char *c = verify_when; *c != 0; c++)
    {
        text[n++] = *c;
    }

    char digits[10];
    unsigned int count = 0;
    unsigned int value = verify_collection;
    do
    {
        digits[count++] = (char)('0' + value % 10);
        value /= 10;
    } while (value != 0);

    while (count > 0)
    {
        text[n++] = digits[--count];
    }

    text[n++] = ')';
    text[n++] = '.';
    text[n] = 0;
    chipsharp_fatal(text);
}

static int in_heap(uintptr_t address)
{
    return address - (uintptr_t)heap_start < (uintptr_t)(heap_end - heap_start);
}

// The header of the object at block must be the address of a class's or an array's type descriptor.
static void verify_type(char *block)
{
    uintptr_t header = *(uintptr_t *)block;
    const chipsharp_type *type = (const chipsharp_type *)header;
    if (header == 0 || header % WORD != 0 || in_heap(header))
    {
        corrupted("an object whose header is not the address of a type", block);
    }

    unsigned int least = (type->flags & TYPE_ARRAY) ? (unsigned int)WORD + 4 : (unsigned int)WORD;
    unsigned int span = (type->flags & TYPE_ARRAY) ? type->element_size : type->size;
    if ((type->flags & ~TYPE_ARRAY) != 0 || type->size < least || type->size > (uintptr_t)(heap_end - heap_start)
        || (!(type->flags & TYPE_ARRAY) && type->element_size != 0) || type->reference_count > span / WORD + 1
        || (type->reference_count != 0 && type->references == 0))
    {
        corrupted("an object whose type descriptor is not one", block);
    }

    for (unsigned int r = 0; r < type->reference_count; r++)
    {
        if (type->references[r] % WORD != 0 || type->references[r] + WORD > span)
        {
            corrupted("an object whose type descriptor is not one", block);
        }
    }

    if ((type->flags & TYPE_ARRAY) && type->element_size != 0
        && length_of(block) > (uintptr_t)(heap_end - heap_start) / type->element_size)
    {
        corrupted("an array longer than the heap", block);
    }
}

// A reference in an object: null, outside the heap (fixed text, an empty array), or the start of an object.
static void verify_reference(uintptr_t reference)
{
    if (reference == 0 || !in_heap(reference))
    {
        return;
    }

    if (reference % GRANULE != 0 || !has_bit(starts, (char *)reference))
    {
        corrupted("a reference into the middle of a block", (void *)reference);
    }

    if (*(uintptr_t *)reference & FREE)
    {
        corrupted("a reference to a free block", (void *)reference);
    }
}

static void verify_references(char *object)
{
    const chipsharp_type *type = type_of(object);
    if (type->flags & TYPE_ARRAY)
    {
        char *element = object + type->size;
        for (unsigned int i = 0; i < length_of(object); i++, element += type->element_size)
        {
            for (unsigned int r = 0; r < type->reference_count; r++)
            {
                verify_reference(*(uintptr_t *)(element + type->references[r]));
            }
        }
    }
    else
    {
        for (unsigned int r = 0; r < type->reference_count; r++)
        {
            verify_reference(*(uintptr_t *)(object + type->references[r]));
        }
    }
}

static void verify(const char *when, unsigned int collection)
{
    verify_when = when;
    verify_collection = collection;

    // every block: its start bit and no other inside it, a header that is a type or a free block's size; then the
    // references of every object, once every block is known to be one
    uintptr_t used = 0;
    uintptr_t blocks = 0;
    for (char *block = heap_start; block < heap_end; blocks++)
    {
        if (!has_bit(starts, block))
        {
            corrupted("a block without its start bit", block);
        }

        uintptr_t header = *(uintptr_t *)block;
        uintptr_t size;
        if (header & FREE)
        {
            size = header & SIZE_BITS;
            if (size < GRANULE || (header & (GRANULE - 1) & ~FREE) != 0)
            {
                corrupted("a free block of no size", block);
            }
        }
        else
        {
            verify_type(block);
            if (has_bit(marks, block))
            {
                corrupted("a mark left after the sweep", block);
            }

            size = block_size(block);
            used += size;
        }

        if (size > (uintptr_t)(heap_end - block))
        {
            corrupted("a block that goes past the end of the heap", block);
        }

        for (char *inside = block + GRANULE; inside < block + size; inside += GRANULE)
        {
            if (has_bit(starts, inside))
            {
                corrupted("a start bit inside a block", inside);
            }
        }

        block += size;
    }

    if (used != used_bytes)
    {
        corrupted("the bytes in use do not add up", heap_start);
    }

    for (char *block = heap_start; block < heap_end; block += block_size(block))
    {
        if (!(*(uintptr_t *)block & FREE))
        {
            verify_references(block);
        }
    }

    // the free lists: free blocks of their size class, each once (no more entries than there are blocks)
    for (unsigned int k = 0; k < CLASSES; k++)
    {
        uintptr_t entries = 0;
        for (free_block *free = lists[k]; free != 0; free = free->next)
        {
            if (!in_heap((uintptr_t)free) || !has_bit(starts, (char *)free) || !(free->header & FREE)
                || size_class(free->header & SIZE_BITS) != k || ++entries > blocks)
            {
                corrupted("a free list that holds something other than free blocks of its size", free);
            }
        }
    }
}

#endif
