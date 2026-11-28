// ChipSharp runtime for the Espressif ESP32-C5: the application description (ESP-IDF's esp_app_desc_t).
//
// ESP-IDF's second-stage bootloader (what Wokwi, and a devkit still flashed with its factory bootloader, runs before an
// app at flash offset 0x10000) reads this structure from the start of the image's first segment (esp_image_format.c,
// verify_segment_header: segment 0 must be at least 256 bytes and start with ESP_APP_DESC_MAGIC_WORD; then
// bootloader_common_check_efuse_blk_validity(min_efuse_blk_rev_full, max_efuse_blk_rev_full) and the mmu_page_size).
// Without it the bootloader reads program code as the structure and refuses the image ("Image requires efuse blk rev
// >= v176.67"). The linker script puts this section first in the code region (the target's "link.head"), so it is the
// first 256 bytes at 0x40800000, which are the first segment's first bytes. The ROM, which loads the image from flash
// offset 0x2000 itself, ignores it: it is plain data in the segment.
//
// The layout is esp_app_desc.h's (components/esp_app_format, release/v5.5), 256 bytes:
//   0   magic_word 0xABCD5432      4   secure_version 0 (no anti-rollback)      8   reserv1[2]
//   16  version[32]                48  project_name[32]                        80  time[16]
//   96  date[16]                   112 idf_ver[32]                             144 app_elf_sha256[32]
//   176 min_efuse_blk_rev_full     178 max_efuse_blk_rev_full                  180 mmu_page_size (log2)
//   181 reserv3[3]                 184 reserv2[18]
// The image writer (ChipSharp.Image.Esp) fills in the two fields only it knows: project_name (the program's name) and
// app_elf_sha256 (the SHA-256 of the .elf, as esptool's elf2image --elf-sha256-offset does). The rest is fixed text, so
// a build is reproducible: the compile time and date are 00:00:00 and "Jan  1 1970", not the build's.
//
//   - min_efuse_blk_rev_full 0 and max_efuse_blk_rev_full 65535: both mean "not set" (bootloader_common_loader.c,
//     IS_FIELD_SET(rev) is rev != 0 && rev != 65535), so no chip's eFuse block revision is refused.
//   - mmu_page_size 16: 64 KB, the log2 ESP-IDF writes for the C5's default CONFIG_MMU_PAGE_SIZE and the page size the
//     bootloader is built with (SPI_FLASH_MMU_PAGE_SIZE 0x10000); any other value in 13..16 would make it reconfigure
//     the MMU, and the image has nothing mapped from flash.

#include <stdint.h>

typedef struct
{
    uint32_t magic_word;
    uint32_t secure_version;
    uint32_t reserv1[2];
    char version[32];
    char project_name[32];
    char time[16];
    char date[16];
    char idf_ver[32];
    uint8_t app_elf_sha256[32];
    uint16_t min_efuse_blk_rev_full;
    uint16_t max_efuse_blk_rev_full;
    uint8_t mmu_page_size;
    uint8_t reserv3[3];
    uint32_t reserv2[18];
} esp_app_desc_t;

_Static_assert(sizeof(esp_app_desc_t) == 256, "esp_app_desc_t is 256 bytes");

__attribute__((section(".esp_app_desc"), used, aligned(16)))
const esp_app_desc_t chipsharp_esp_app_desc =
{
    .magic_word = 0xABCD5432u,
    .secure_version = 0,
    .version = "1.0",
    .project_name = "chipsharp",
    .time = "00:00:00",
    .date = "Jan  1 1970",
    .idf_ver = "ChipSharp",
    .min_efuse_blk_rev_full = 0,
    .max_efuse_blk_rev_full = 65535,
    .mmu_page_size = 16,
};
