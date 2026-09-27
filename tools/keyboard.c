// Presses keys and types text through a wlroots compositor's virtual keyboard protocol, for driving
// Fermata as a Wayland client in tools/isolated-session.sh (tools/xinput.c reaches only X11 clients).
//
//   keyboard key KEYSYM [N]     press a key N times (e.g. Page_Down, space, ctrl+q, shift+n, alt+Left)
//   keyboard type TEXT          type the text, which must be characters of the US layout
//
// The keys go to the focused window, with a US layout keymap. Build from the repository root:
//   wayland-scanner client-header tools/protocols/virtual-keyboard-unstable-v1.xml virtual-keyboard.h
//   wayland-scanner private-code tools/protocols/virtual-keyboard-unstable-v1.xml virtual-keyboard.c
//   cc -O2 -I. -o keyboard tools/keyboard.c virtual-keyboard.c -lwayland-client -lxkbcommon
#define _GNU_SOURCE
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/mman.h>
#include <time.h>
#include <unistd.h>
#include <wayland-client.h>
#include <xkbcommon/xkbcommon.h>
#include "virtual-keyboard.h"

static struct wl_seat *seat;
static struct zwp_virtual_keyboard_manager_v1 *manager;

static void global(void *data, struct wl_registry *registry, uint32_t name, const char *interface, uint32_t version)
{
    if (!strcmp(interface, wl_seat_interface.name) && !seat)
        seat = wl_registry_bind(registry, name, &wl_seat_interface, 1);
    else if (!strcmp(interface, zwp_virtual_keyboard_manager_v1_interface.name))
        manager = wl_registry_bind(registry, name, &zwp_virtual_keyboard_manager_v1_interface, 1);
}
static void global_remove(void *data, struct wl_registry *registry, uint32_t name) {}
static const struct wl_registry_listener registry_listener = { global, global_remove };

static struct wl_display *display;
static struct zwp_virtual_keyboard_v1 *keyboard;
static struct xkb_keymap *keymap;

static uint32_t now_ms(void)
{
    struct timespec ts;
    clock_gettime(CLOCK_MONOTONIC, &ts);
    return (uint32_t)(ts.tv_sec * 1000 + ts.tv_nsec / 1000000);
}

// Finds the key and shift level that produce a keysym in the first layout.
static int find_key(xkb_keysym_t sym, xkb_keycode_t *code, int *shifted)
{
    for (xkb_keycode_t key = xkb_keymap_min_keycode(keymap); key <= xkb_keymap_max_keycode(keymap); key++) {
        for (int level = 0; level < 2; level++) {
            const xkb_keysym_t *syms;
            int count = xkb_keymap_key_get_syms_by_level(keymap, key, 0, level, &syms);
            for (int i = 0; i < count; i++) {
                if (syms[i] == sym) {
                    *code = key;
                    *shifted = level;
                    return 1;
                }
            }
        }
    }
    return 0;
}

static uint32_t mask(const char *modifier)
{
    xkb_mod_index_t index = xkb_keymap_mod_get_index(keymap, modifier);
    return index == XKB_MOD_INVALID ? 0 : 1u << index;
}

static void send_key(xkb_keycode_t code, uint32_t state)
{
    // Wayland key codes are evdev codes, which XKB offsets by 8.
    zwp_virtual_keyboard_v1_key(keyboard, now_ms(), code - 8, state);
}

// Presses and releases a key with modifiers held, the way a physical keyboard reports it.
static void press(xkb_keycode_t code, const xkb_keycode_t *modifier_keys, int modifier_count, uint32_t modifiers)
{
    for (int i = 0; i < modifier_count; i++)
        send_key(modifier_keys[i], WL_KEYBOARD_KEY_STATE_PRESSED);
    if (modifiers)
        zwp_virtual_keyboard_v1_modifiers(keyboard, modifiers, 0, 0, 0);
    send_key(code, WL_KEYBOARD_KEY_STATE_PRESSED);
    send_key(code, WL_KEYBOARD_KEY_STATE_RELEASED);
    for (int i = modifier_count - 1; i >= 0; i--)
        send_key(modifier_keys[i], WL_KEYBOARD_KEY_STATE_RELEASED);
    if (modifiers)
        zwp_virtual_keyboard_v1_modifiers(keyboard, 0, 0, 0, 0);
    wl_display_roundtrip(display);
    usleep(30 * 1000);
}

// Presses a key given as a keysym name, optionally prefixed with ctrl+, shift+ and alt+.
static int press_named(const char *spec)
{
    uint32_t modifiers = 0;
    xkb_keycode_t modifier_keys[3];
    int modifier_count = 0;
    const char *name = spec;
    for (;;) {
        const char *plus = strchr(name, '+');
        if (!plus || plus == name)
            break;
        size_t length = (size_t)(plus - name);
        const char *keysym = NULL, *modifier = NULL;
        if (length == 4 && !strncmp(name, "ctrl", 4))
            keysym = "Control_L", modifier = XKB_MOD_NAME_CTRL;
        else if (length == 5 && !strncmp(name, "shift", 5))
            keysym = "Shift_L", modifier = XKB_MOD_NAME_SHIFT;
        else if (length == 3 && !strncmp(name, "alt", 3))
            keysym = "Alt_L", modifier = XKB_MOD_NAME_ALT;
        else
            break;
        int level;
        if (modifier_count < 3 && find_key(xkb_keysym_from_name(keysym, XKB_KEYSYM_NO_FLAGS), &modifier_keys[modifier_count], &level))
            modifier_count++;
        modifiers |= mask(modifier);
        name = plus + 1;
    }
    xkb_keycode_t code;
    int shifted;
    xkb_keysym_t sym = xkb_keysym_from_name(name, XKB_KEYSYM_NO_FLAGS);
    if (sym == XKB_KEY_NoSymbol || !find_key(sym, &code, &shifted)) {
        fprintf(stderr, "unknown key %s\n", name);
        return 0;
    }
    if (shifted && !(modifiers & mask(XKB_MOD_NAME_SHIFT))) {
        int level;
        if (modifier_count < 3 && find_key(XKB_KEY_Shift_L, &modifier_keys[modifier_count], &level))
            modifier_count++;
        modifiers |= mask(XKB_MOD_NAME_SHIFT);
    }
    press(code, modifier_keys, modifier_count, modifiers);
    return 1;
}

int main(int argc, char **argv)
{
    if (argc < 3 || (strcmp(argv[1], "key") && strcmp(argv[1], "type"))) {
        fprintf(stderr, "usage: keyboard key KEYSYM [N] | type TEXT\n");
        return 2;
    }
    display = wl_display_connect(NULL);
    if (!display) {
        fprintf(stderr, "no Wayland display\n");
        return 1;
    }
    struct wl_registry *registry = wl_display_get_registry(display);
    wl_registry_add_listener(registry, &registry_listener, NULL);
    wl_display_roundtrip(display);
    if (!manager || !seat) {
        fprintf(stderr, "the compositor offers no virtual keyboard\n");
        return 1;
    }

    struct xkb_context *context = xkb_context_new(XKB_CONTEXT_NO_FLAGS);
    struct xkb_rule_names names = { .layout = "us" };
    keymap = context ? xkb_keymap_new_from_names(context, &names, XKB_KEYMAP_COMPILE_NO_FLAGS) : NULL;
    char *text = keymap ? xkb_keymap_get_as_string(keymap, XKB_KEYMAP_FORMAT_TEXT_V1) : NULL;
    if (!text) {
        fprintf(stderr, "could not compile a US keymap\n");
        return 1;
    }
    size_t size = strlen(text) + 1;
    int fd = memfd_create("keymap", MFD_CLOEXEC);
    if (fd < 0 || write(fd, text, size) != (ssize_t)size) {
        perror("keymap");
        return 1;
    }
    keyboard = zwp_virtual_keyboard_manager_v1_create_virtual_keyboard(manager, seat);
    zwp_virtual_keyboard_v1_keymap(keyboard, WL_KEYBOARD_KEYMAP_FORMAT_XKB_V1, fd, (uint32_t)size);
    wl_display_roundtrip(display);
    // Let the focused client take the new keymap before the first key.
    usleep(100 * 1000);

    int ok = 1;
    if (!strcmp(argv[1], "key")) {
        int times = argc >= 4 ? atoi(argv[3]) : 1;
        for (int i = 0; i < times && ok; i++)
            ok = press_named(argv[2]);
    } else {
        for (const char *c = argv[2]; *c && ok; c++) {
            xkb_keycode_t code;
            int shifted;
            if (!find_key(xkb_utf32_to_keysym((uint32_t)(unsigned char)*c), &code, &shifted)) {
                fprintf(stderr, "cannot type '%c'\n", *c);
                ok = 0;
                break;
            }
            xkb_keycode_t shift_key;
            int level;
            int has_shift = shifted && find_key(XKB_KEY_Shift_L, &shift_key, &level);
            press(code, &shift_key, has_shift, has_shift ? mask(XKB_MOD_NAME_SHIFT) : 0);
        }
    }
    zwp_virtual_keyboard_v1_destroy(keyboard);
    wl_display_roundtrip(display);
    wl_display_disconnect(display);
    return ok ? 0 : 1;
}
