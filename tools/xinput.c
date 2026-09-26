// Synthesizes pointer and keyboard input for X11 clients through the XTEST extension, for
// driving Fermata in tools/isolated-session.sh. Build: cc -O2 -o xinput tools/xinput.c -lX11 -lXtst
//
//   xinput click X Y            left click at a root-window position
//   xinput move X Y             move the pointer
//   xinput wheel X Y N          scroll N notches down (negative: up) with the pointer at X Y
//   xinput key KEYSYM [N]       press a key N times (e.g. Page_Down, space, ctrl+End)
//
// XWayland ignores synthesized pointer motion, and drops the first synthesized key event after a
// window maps, so send a harmless key (xinput key Shift_L) before the first real shortcut.
#include <X11/Xlib.h>
#include <X11/extensions/XTest.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>

static void pause_ms(int ms) { usleep(ms * 1000); }

int main(int argc, char **argv)
{
    Display *display = XOpenDisplay(NULL);
    if (!display || argc < 2) {
        fprintf(stderr, "usage: xinput click|move|wheel|key …\n");
        return 2;
    }
    int major, minor, event, error;
    if (!XTestQueryExtension(display, &event, &error, &major, &minor)) {
        fprintf(stderr, "XTEST is not available\n");
        return 1;
    }
    const char *verb = argv[1];
    if ((!strcmp(verb, "click") || !strcmp(verb, "move") || !strcmp(verb, "wheel")) && argc >= 4) {
        int x = atoi(argv[2]), y = atoi(argv[3]);
        XTestFakeMotionEvent(display, -1, x, y, CurrentTime);
        XFlush(display);
        pause_ms(60);
        if (!strcmp(verb, "click")) {
            XTestFakeButtonEvent(display, 1, True, CurrentTime);
            XFlush(display);
            pause_ms(40);
            XTestFakeButtonEvent(display, 1, False, CurrentTime);
        } else if (!strcmp(verb, "wheel")) {
            int notches = argc >= 5 ? atoi(argv[4]) : 1;
            unsigned button = notches < 0 ? 4 : 5;
            for (int i = 0; i < abs(notches); i++) {
                XTestFakeButtonEvent(display, button, True, CurrentTime);
                XTestFakeButtonEvent(display, button, False, CurrentTime);
                XFlush(display);
                pause_ms(16);
            }
        }
    } else if (!strcmp(verb, "key") && argc >= 3) {
        int times = argc >= 4 ? atoi(argv[3]) : 1;
        char name[64];
        strncpy(name, argv[2], sizeof name - 1);
        name[sizeof name - 1] = 0;
        KeyCode modifier = 0;
        char *plus = strchr(name, '+');
        char *key = name;
        if (plus) {
            *plus = 0;
            modifier = XKeysymToKeycode(display, XStringToKeysym(!strcmp(name, "ctrl") ? "Control_L" : !strcmp(name, "shift") ? "Shift_L" : "Alt_L"));
            key = plus + 1;
        }
        KeyCode code = XKeysymToKeycode(display, XStringToKeysym(key));
        if (!code) {
            fprintf(stderr, "unknown key %s\n", key);
            return 1;
        }
        for (int i = 0; i < times; i++) {
            if (modifier)
                XTestFakeKeyEvent(display, modifier, True, CurrentTime);
            XTestFakeKeyEvent(display, code, True, CurrentTime);
            XTestFakeKeyEvent(display, code, False, CurrentTime);
            if (modifier)
                XTestFakeKeyEvent(display, modifier, False, CurrentTime);
            XFlush(display);
            pause_ms(30);
        }
    } else {
        fprintf(stderr, "bad arguments\n");
        return 2;
    }
    XFlush(display);
    XCloseDisplay(display);
    return 0;
}
