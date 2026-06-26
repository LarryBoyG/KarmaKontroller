/*
 * Karma Kontroller WinUSB driver helper.
 *
 * This small helper uses libwdi dynamically to generate and install a WinUSB
 * driver package for the GoPro Karma Controller update-mode USB device.
 */

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include "libwdi.h"

#define KARMA_VID 0x1B8E
#define KARMA_PID 0xC003
#define KARMA_DESC "GoPro Karma Controller Update Mode (WinUSB)"
#define KARMA_VENDOR "KarmaKontroller"
#define KARMA_INF "KarmaWinUSB.inf"

static void usage(void)
{
    printf("karma-winusb-driver --dest <directory> [--extract-only]\n");
}

static const char* arg_value(int argc, char** argv, const char* name)
{
    int i;
    for (i = 1; i + 1 < argc; i++) {
        if (strcmp(argv[i], name) == 0) {
            return argv[i + 1];
        }
    }
    return NULL;
}

static int has_arg(int argc, char** argv, const char* name)
{
    int i;
    for (i = 1; i < argc; i++) {
        if (strcmp(argv[i], name) == 0) {
            return 1;
        }
    }
    return 0;
}

int main(int argc, char** argv)
{
    const char* dest = arg_value(argc, argv, "--dest");
    int extract_only = has_arg(argc, argv, "--extract-only");
    int result;
    int matched = 0;
    struct wdi_device_info dev = { 0 };
    struct wdi_device_info* list = NULL;
    struct wdi_device_info* item = NULL;
    struct wdi_options_create_list create_options = { 0 };
    struct wdi_options_prepare_driver prepare_options = { 0 };
    struct wdi_options_install_driver install_options = { 0 };

    if (dest == NULL || strlen(dest) == 0) {
        usage();
        return WDI_ERROR_INVALID_PARAM;
    }

    dev.vid = KARMA_VID;
    dev.pid = KARMA_PID;
    dev.desc = KARMA_DESC;
    prepare_options.driver_type = WDI_WINUSB;
    prepare_options.vendor_name = KARMA_VENDOR;
    create_options.list_all = TRUE;
    create_options.list_hubs = TRUE;
    create_options.trim_whitespaces = TRUE;
    install_options.pending_install_timeout = 120000;

    wdi_set_log_level(WDI_LOG_LEVEL_DEBUG);

    printf("Preparing WinUSB driver package for %04X:%04X...\n", KARMA_VID, KARMA_PID);
    result = wdi_prepare_driver(&dev, dest, KARMA_INF, &prepare_options);
    printf("prepare: %s (%d)\n", wdi_strerror(result), result);
    if (result != WDI_SUCCESS || extract_only) {
        return result;
    }

    printf("Searching for connected Karma controller...\n");
    result = wdi_create_list(&list, &create_options);
    if (result == WDI_SUCCESS) {
        for (item = list; item != NULL; item = item->next) {
            if (item->vid == dev.vid && item->pid == dev.pid && item->mi == dev.mi && item->is_composite == dev.is_composite) {
                dev.hardware_id = item->hardware_id;
                dev.device_id = item->device_id;
                matched = 1;
                printf("matched: %s\n", dev.hardware_id ? dev.hardware_id : "(no hardware id)");
                result = wdi_install_driver(&dev, dest, KARMA_INF, &install_options);
                printf("install: %s (%d)\n", wdi_strerror(result), result);
                break;
            }
        }
        wdi_destroy_list(list);
    } else {
        printf("device list: %s (%d)\n", wdi_strerror(result), result);
    }

    if (!matched) {
        printf("No connected device matched; preinstalling driver package.\n");
        result = wdi_install_driver(&dev, dest, KARMA_INF, &install_options);
        printf("install: %s (%d)\n", wdi_strerror(result), result);
    }

    return result;
}
