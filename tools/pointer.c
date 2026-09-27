// Moves, clicks, drags and scrolls the pointer of a wlroots compositor through its virtual pointer
// protocol, for driving Fermata in tools/isolated-session.sh.
//
//   pointer move X Y
//   pointer hover X Y MS               rest there for MS milliseconds, moving a pixel back and forth
//   pointer click X Y [right|middle]
//   pointer doubleclick X Y
//   pointer drag X1 Y1 X2 Y2 [STEPS] [HOLD_MS]
//                                      press at X1 Y1, move there in STEPS, hold, release at X2 Y2
//   pointer wheel X Y N                N notches down (negative: up)
//
// Coordinates are in pixels of the (single) output. The virtual pointer exists only while the tool runs,
// and a Wayland client sees it leave when the tool exits, so take a screenshot of a hover state while
// "pointer hover" is still running. Build from the repository root:
//   wayland-scanner client-header tools/protocols/wlr-virtual-pointer-unstable-v1.xml virtual-pointer.h
//   wayland-scanner private-code tools/protocols/wlr-virtual-pointer-unstable-v1.xml virtual-pointer.c
//   cc -O2 -I. -o pointer tools/pointer.c virtual-pointer.c -lwayland-client
#include <linux/input-event-codes.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>
#include <unistd.h>
#include <wayland-client.h>
#include "virtual-pointer.h"

static struct wl_seat *seat;
static struct wl_output *output;
static struct zwlr_virtual_pointer_manager_v1 *manager;
static int width, height;

static void output_geometry(void *data, struct wl_output *o, int32_t x, int32_t y, int32_t pw, int32_t ph,
                            int32_t subpixel, const char *make, const char *model, int32_t transform) {}
static void output_mode(void *data, struct wl_output *o, uint32_t flags, int32_t w, int32_t h, int32_t refresh)
{
    if (flags & WL_OUTPUT_MODE_CURRENT) {
        width = w;
        height = h;
    }
}
static void output_done(void *data, struct wl_output *o) {}
static void output_scale(void *data, struct wl_output *o, int32_t factor) {}
static void output_name(void *data, struct wl_output *o, const char *name) {}
static void output_description(void *data, struct wl_output *o, const char *description) {}
static const struct wl_output_listener output_listener = {
    output_geometry, output_mode, output_done, output_scale, output_name, output_description,
};

static void global(void *data, struct wl_registry *registry, uint32_t name, const char *interface, uint32_t version)
{
    if (!strcmp(interface, wl_seat_interface.name))
        seat = wl_registry_bind(registry, name, &wl_seat_interface, 1);
    else if (!strcmp(interface, wl_output_interface.name) && !output) {
        output = wl_registry_bind(registry, name, &wl_output_interface, 2);
        wl_output_add_listener(output, &output_listener, NULL);
    } else if (!strcmp(interface, zwlr_virtual_pointer_manager_v1_interface.name))
        manager = wl_registry_bind(registry, name, &zwlr_virtual_pointer_manager_v1_interface, 1);
}
static void global_remove(void *data, struct wl_registry *registry, uint32_t name) {}
static const struct wl_registry_listener registry_listener = { global, global_remove };

static struct wl_display *display;
static struct zwlr_virtual_pointer_v1 *pointer;

static uint32_t now_ms(void)
{
    struct timespec ts;
    clock_gettime(CLOCK_MONOTONIC, &ts);
    return (uint32_t)(ts.tv_sec * 1000 + ts.tv_nsec / 1000000);
}

static void send(int pause_ms)
{
    zwlr_virtual_pointer_v1_frame(pointer);
    wl_display_roundtrip(display);
    usleep(pause_ms * 1000);
}

static void move_to(double x, double y, int pause_ms)
{
    zwlr_virtual_pointer_v1_motion_absolute(pointer, now_ms(), (uint32_t)x, (uint32_t)y, width, height);
    send(pause_ms);
}

static void button(uint32_t code, uint32_t state, int pause_ms)
{
    zwlr_virtual_pointer_v1_button(pointer, now_ms(), code, state);
    send(pause_ms);
}

int main(int argc, char **argv)
{
    if (argc < 4) {
        fprintf(stderr, "usage: pointer move X Y | click X Y [right|middle] | doubleclick X Y | hover X Y MS | drag X1 Y1 X2 Y2 [STEPS] [HOLD_MS] | wheel X Y N\n");
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
    wl_display_roundtrip(display);
    if (!manager || !seat || width <= 0) {
        fprintf(stderr, "the compositor offers no virtual pointer or output\n");
        return 1;
    }
    pointer = zwlr_virtual_pointer_manager_v1_create_virtual_pointer(manager, seat);

    const char *verb = argv[1];
    double x = atof(argv[2]), y = atof(argv[3]);
    if (!strcmp(verb, "move")) {
        move_to(x, y, 50);
    } else if (!strcmp(verb, "hover") && argc >= 5) {
        int duration = atoi(argv[4]);
        for (int elapsed = 0; elapsed < duration; elapsed += 50)
            move_to(x + (elapsed / 50) % 2, y, 50);
    } else if (!strcmp(verb, "click")) {
        uint32_t code = argc < 5 ? BTN_LEFT : !strcmp(argv[4], "right") ? BTN_RIGHT : BTN_MIDDLE;
        move_to(x, y, 80);
        button(code, WL_POINTER_BUTTON_STATE_PRESSED, 40);
        button(code, WL_POINTER_BUTTON_STATE_RELEASED, 40);
    } else if (!strcmp(verb, "doubleclick")) {
        move_to(x, y, 80);
        for (int i = 0; i < 2; i++) {
            button(BTN_LEFT, WL_POINTER_BUTTON_STATE_PRESSED, 30);
            button(BTN_LEFT, WL_POINTER_BUTTON_STATE_RELEASED, 60);
        }
    } else if (!strcmp(verb, "drag") && argc >= 6) {
        double x2 = atof(argv[4]), y2 = atof(argv[5]);
        int steps = argc >= 7 ? atoi(argv[6]) : 20;
        int hold = argc >= 8 ? atoi(argv[7]) : 150;
        move_to(x, y, 80);
        button(BTN_LEFT, WL_POINTER_BUTTON_STATE_PRESSED, 80);
        for (int i = 1; i <= steps; i++)
            move_to(x + (x2 - x) * i / steps, y + (y2 - y) * i / steps, 25);
        usleep(hold * 1000);
        button(BTN_LEFT, WL_POINTER_BUTTON_STATE_RELEASED, 80);
    } else if (!strcmp(verb, "wheel") && argc >= 5) {
        int notches = atoi(argv[4]);
        move_to(x, y, 60);
        for (int i = 0; i < abs(notches); i++) {
            zwlr_virtual_pointer_v1_axis_source(pointer, WL_POINTER_AXIS_SOURCE_WHEEL);
            zwlr_virtual_pointer_v1_axis_discrete(pointer, now_ms(), WL_POINTER_AXIS_VERTICAL_SCROLL,
                                                  wl_fixed_from_int(notches < 0 ? -15 : 15), notches < 0 ? -1 : 1);
            send(16);
        }
    } else {
        fprintf(stderr, "bad arguments\n");
        return 2;
    }
    zwlr_virtual_pointer_v1_destroy(pointer);
    wl_display_roundtrip(display);
    wl_display_disconnect(display);
    return 0;
}
