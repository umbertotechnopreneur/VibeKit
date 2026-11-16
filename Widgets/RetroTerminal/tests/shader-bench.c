// SPDX-License-Identifier: MIT
// A hidden GLX drawable owns the context; all measured draws target a real FBO.
#define GL_GLEXT_PROTOTYPES
#include <GL/gl.h>
#include <GL/glext.h>
#include <GL/glx.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

static void require(int ok, const char *message) {
    if (!ok) { fprintf(stderr, "%s\n", message); exit(1); }
}
static unsigned char *read_file(const char *path, size_t *length) {
    FILE *f = fopen(path, "rb"); require(f != NULL, "Cannot open input");
    require(fseek(f, 0, SEEK_END) == 0, "Cannot seek input");
    long size = ftell(f); require(size > 0, "Empty input"); rewind(f);
    unsigned char *data = calloc((size_t)size + 1, 1); require(data != NULL, "Allocation failed");
    require(fread(data, 1, (size_t)size, f) == (size_t)size, "Cannot read input");
    fclose(f); *length = (size_t)size; return data;
}
static void draw(void) {
    glBegin(GL_QUADS);
    glTexCoord2f(0, 0); glVertex2f(-1, -1);
    glTexCoord2f(1, 0); glVertex2f(1, -1);
    glTexCoord2f(1, 1); glVertex2f(1, 1);
    glTexCoord2f(0, 1); glVertex2f(-1, 1);
    glEnd();
}
static void save_rgb(const char *directory, const char *name, const unsigned char *rgba, int w, int h) {
    char path[4096];
    require(snprintf(path, sizeof(path), "%s/%s.ppm", directory, name) < (int)sizeof(path), "Output path too long");
    FILE *f = fopen(path, "wb"); require(f != NULL, "Cannot open output");
    fprintf(f, "P6\n%d %d\n255\n", w, h);
    // Pack one row at a time; do not make millions of tiny stdio calls.
    // Both upload and readback use the same row order for deterministic comparisons.
    unsigned char *row = malloc((size_t)w * 3); require(row != NULL, "Output row allocation failed");
    for (int y = 0; y < h; y++) {
        for (int x = 0; x < w; x++) memcpy(row + x * 3, rgba + ((size_t)y * w + x) * 4, 3);
        require(fwrite(row, 3, (size_t)w, f) == (size_t)w, "Cannot write output");
    }
    free(row);
    require(fclose(f) == 0, "Cannot close output");
}
static int compare_double(const void *a, const void *b) {
    double x = *(const double *)a, y = *(const double *)b;
    return (x > y) - (x < y);
}
static GLint uniform(GLuint program, const char *name) {
    GLint location = glGetUniformLocation(program, name);
    if (location < 0) { fprintf(stderr, "Required production uniform missing: %s\n", name); exit(1); }
    return location;
}
static void read_pixels(unsigned char *pixels, int width, int height) {
    draw();
    glReadPixels(0, 0, width, height, GL_RGBA, GL_UNSIGNED_BYTE, pixels);
    require(glGetError() == GL_NO_ERROR, "OpenGL error during output verification");
}
static size_t different_pixels(const unsigned char *a, const unsigned char *b, size_t count, int tolerance) {
    size_t changed = 0;
    for (size_t pixel = 0; pixel < count; pixel++) {
        for (int channel = 0; channel < 3; channel++) {
            if (abs((int)a[pixel * 4 + channel] - (int)b[pixel * 4 + channel]) > tolerance) {
                changed++;
                break;
            }
        }
    }
    return changed;
}
static void defaults(GLuint program) {
    glUniform4f(uniform(program, "crtStyle"), 2, 1, 0, 0);
    glUniform2f(uniform(program, "crtFinish"), 0, 0);
    glUniform4f(uniform(program, "crtPhosphor"), .2f, 1, .42f, 0);
    glUniform4f(uniform(program, "crtRaster"), 3, 1, 0, .16f);
    glUniform4f(uniform(program, "crtOptics"), 2, 0, 1, 0);
    glUniform4f(uniform(program, "crtGlitch"), 0, 0, 0, .5f);
}
int main(int argc, char **argv) {
    setvbuf(stdout, NULL, _IOLBF, 0);
    require(argc == 6, "Usage: shader-bench shader.frag source.rgba output-dir width height");
    int width = atoi(argv[4]), height = atoi(argv[5]);
    require(width >= 64 && height >= 64 && width <= 3840 && height <= 2160, "Dimensions outside test limits");
    size_t bytes = (size_t)width * height * 4, input_bytes, shader_bytes;
    unsigned char *input = read_file(argv[2], &input_bytes);
    require(input_bytes == bytes, "RGBA input size mismatch");
    unsigned char *output = malloc(bytes); require(output != NULL, "Allocation failed");
    const char *shader_source = (const char *)read_file(argv[1], &shader_bytes);
    Display *display = XOpenDisplay(NULL); require(display != NULL, "No X display (use Xwayland or xvfb-run for software checking)");
    int attributes[] = {GLX_RGBA, None};
    XVisualInfo *visual = glXChooseVisual(display, DefaultScreen(display), attributes);
    require(visual != NULL, "No GLX visual");
    GLXContext context = glXCreateContext(display, visual, NULL, True);
    require(context != NULL, "Cannot create GLX context");
    Colormap colormap = XCreateColormap(display, RootWindow(display, visual->screen), visual->visual, AllocNone);
    XSetWindowAttributes wa = {.colormap = colormap};
    Window window = XCreateWindow(display, RootWindow(display, visual->screen), 0, 0, 1, 1, 0,
        visual->depth, InputOutput, visual->visual, CWColormap, &wa);
    require(glXMakeCurrent(display, window, context), "Cannot activate GLX context");
    printf("Renderer: %s\nOpenGL: %s\n", glGetString(GL_RENDERER), glGetString(GL_VERSION));
    GLuint shader = glCreateShader(GL_FRAGMENT_SHADER);
    glShaderSource(shader, 1, &shader_source, NULL); glCompileShader(shader);
    GLint ok; glGetShaderiv(shader, GL_COMPILE_STATUS, &ok);
    if (!ok) { char log[8192]; glGetShaderInfoLog(shader, sizeof(log), NULL, log); fprintf(stderr, "%s\n", log); }
    require(ok, "Fragment shader compile failed");
    GLuint program = glCreateProgram(); glAttachShader(program, shader); glLinkProgram(program);
    glGetProgramiv(program, GL_LINK_STATUS, &ok);
    if (!ok) { char log[8192]; glGetProgramInfoLog(program, sizeof(log), NULL, log); fprintf(stderr, "%s\n", log); }
    require(ok, "Shader link failed"); glUseProgram(program);
    GLuint textures[2]; glGenTextures(2, textures);
    for (int i = 0; i < 2; i++) {
        glBindTexture(GL_TEXTURE_2D, textures[i]);
        glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MIN_FILTER, GL_LINEAR);
        glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MAG_FILTER, GL_LINEAR);
        glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_S, GL_CLAMP_TO_EDGE);
        glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_T, GL_CLAMP_TO_EDGE);
        glTexImage2D(GL_TEXTURE_2D, 0, GL_RGBA8, width, height, 0, GL_RGBA, GL_UNSIGNED_BYTE, i == 0 ? input : NULL);
    }
    GLuint framebuffer; glGenFramebuffers(1, &framebuffer); glBindFramebuffer(GL_FRAMEBUFFER, framebuffer);
    glFramebufferTexture2D(GL_FRAMEBUFFER, GL_COLOR_ATTACHMENT0, GL_TEXTURE_2D, textures[1], 0);
    require(glCheckFramebufferStatus(GL_FRAMEBUFFER) == GL_FRAMEBUFFER_COMPLETE, "Incomplete offscreen framebuffer");
    glDrawBuffer(GL_COLOR_ATTACHMENT0); glReadBuffer(GL_COLOR_ATTACHMENT0);
    glBindTexture(GL_TEXTURE_2D, textures[0]); glViewport(0, 0, width, height);
    glDisable(GL_DITHER); glDisable(GL_BLEND); glDisable(GL_DEPTH_TEST); glColor4f(1, 1, 1, 1);
    glUniform1i(uniform(program, "desktopTexture"), 0);
    glUniform2f(uniform(program, "crtSize"), width, height);
    defaults(program);
    glUniform2f(uniform(program, "crtFinish"), .25f, .015f);
    const char *names[] = {"passthrough", "green", "amber", "arcade", "glitch-rgb", "glitch-tear", "glitch-noise", "glitch-band"};
    printf("Target: %dx%d RGBA8 FBO; 12 warmup + 60 timed draws per mode\n", width, height);
    printf("GPU elapsed times exclude capture, GNOME composition, presentation and CPU time.\n");
    for (int mode = 0; mode < 8; mode++) {
        glUniform4f(uniform(program, "crtStyle"), mode >= 4 ? 2 : mode ? mode - 1 : 0, mode ? .9f : 0, .22f, .35f);
        glUniform4f(uniform(program, "crtGlitch"), mode >= 4 ? mode - 3 : 0, .5f, .8f, .37f);
        glClearColor(1, 0, 1, 1); glClear(GL_COLOR_BUFFER_BIT);
        for (int i = 0; i < 12; i++) draw();
        glFinish();
        double batches[3];
        GLuint query; glGenQueries(1, &query);
        for (int batch = 0; batch < 3; batch++) {
            glBeginQuery(GL_TIME_ELAPSED, query);
            for (int i = 0; i < 20; i++) draw();
            glEndQuery(GL_TIME_ELAPSED);
            GLuint64 nanoseconds; glGetQueryObjectui64v(query, GL_QUERY_RESULT, &nanoseconds);
            require(nanoseconds > 0, "Invalid GPU timer result");
            batches[batch] = (double)nanoseconds / 20.0 / 1000000.0;
        }
        glDeleteQueries(1, &query);
        glReadPixels(0, 0, width, height, GL_RGBA, GL_UNSIGNED_BYTE, output);
        require(glGetError() == GL_NO_ERROR, "OpenGL error during benchmark");
        int max_error = 0; size_t changed = 0;
        for (size_t i = 0; i < bytes; i++) {
            int difference = abs((int)input[i] - (int)output[i]);
            if (difference > max_error) max_error = difference;
            if (difference > 1) changed++;
        }
        if (mode == 0) require(max_error <= 1, "Intensity zero did not preserve input pixels");
        else require(changed > (size_t)width * height / 4, "Effect did not alter enough pixels; invalid render");
        save_rgb(argv[3], names[mode], output, width, height);
        qsort(batches, 3, sizeof(double), compare_double);
        printf("%s: median batch-average %.4f ms/draw; batch range %.4f–%.4f; max channel delta %d\n",
            names[mode], batches[1], batches[0], batches[2], max_error);
    }
    // Isolate each setting on a flat image or the synthetic source. These checks
    // verify observable output, not a duplicate CPU implementation of the GLSL.
    unsigned char *baseline = malloc(bytes), *flat = malloc(bytes);
    require(baseline && flat, "Verification allocation failed");
    for (size_t i = 0; i < bytes; i++) flat[i] = i % 4 == 3 ? 255 : 128;
    glTexSubImage2D(GL_TEXTURE_2D, 0, 0, 0, width, height, GL_RGBA, GL_UNSIGNED_BYTE, flat);
    defaults(program);
    glUniform4f(uniform(program, "crtStyle"), 2, 1, .6f, 0);
    glUniform4f(uniform(program, "crtRaster"), 4, 1, 0, 0);
    read_pixels(baseline, width, height);
    int minimum = 255, maximum = 0;
    for (int row = 0; row < 12; row++) {
        int value = baseline[((size_t)(height/2 + row) * width + width/2) * 4];
        if (value < minimum) minimum = value;
        if (value > maximum) maximum = value;
    }
    printf("Flat-source scanline row contrast: %d/255\n", maximum - minimum);
    require(maximum - minimum > 40, "Scanline contrast disappeared at native pixel centers");
    glUniform4f(uniform(program, "crtRaster"), 4, 2, 0, 0);
    read_pixels(output, width, height);
    require(different_pixels(baseline, output, (size_t)width * height, 2) > (size_t)width * height / 8,
        "Scanline width did not affect output");
    glUniform4f(uniform(program, "crtRaster"), 6, 2, 0, 0);
    read_pixels(output, width, height);
    for (int row = 0; row < 6; row++) {
        size_t index = ((size_t)(height/2 + row) * width + width/2) * 4;
        require(abs((int)output[index] - (int)output[index + (size_t)width * 6 * 4]) <= 1,
            "Scanline spacing does not repeat at the selected period");
    }
    defaults(program);
    glUniform4f(uniform(program, "crtStyle"), 0, 1, 0, 0);
    glUniform4f(uniform(program, "crtPhosphor"), 1, .05f, .02f, 1);
    read_pixels(output, width, height);
    size_t center = ((size_t)(height/2) * width + width/2) * 4;
    require(output[center] > 80 && output[center + 1] < 20 && output[center + 2] < 20,
        "Custom red phosphor did not replace the default palette");
    glUniform4f(uniform(program, "crtPhosphor"), .02f, .8f, 1, 1);
    read_pixels(output, width, height);
    require(output[center] < 20 && output[center + 1] > 70 && output[center + 2] > 80,
        "Custom cyan phosphor channels are incorrect");
    save_rgb(argv[3], "custom-phosphor", output, width, height);
    defaults(program);
    read_pixels(baseline, width, height);
    glUniform4f(uniform(program, "crtRaster"), 3, 1, 1, .6f);
    read_pixels(output, width, height);
    require(different_pixels(baseline, output, (size_t)width * height, 2) > (size_t)width * height / 4,
        "Stripe phosphor mask did not affect output");
    memcpy(baseline, output, bytes);
    glUniform4f(uniform(program, "crtRaster"), 3, 1, 2, .6f);
    read_pixels(output, width, height);
    require(different_pixels(baseline, output, (size_t)width * height, 2) > (size_t)width * height / 8,
        "Dot phosphor mask is indistinguishable from stripes");
    save_rgb(argv[3], "mask-dots", output, width, height);
    defaults(program);
    read_pixels(baseline, width, height);
    glUniform4f(uniform(program, "crtOptics"), 2, .12f, 1, 0);
    read_pixels(output, width, height);
    require(output[center] > baseline[center] + 10, "Black-level lift did not raise gray output");
    glTexSubImage2D(GL_TEXTURE_2D, 0, 0, 0, width, height, GL_RGBA, GL_UNSIGNED_BYTE, input);
    defaults(program);
    read_pixels(baseline, width, height);
    glUniform4f(uniform(program, "crtOptics"), 2, 0, 1.7f, 0);
    read_pixels(output, width, height);
    require(different_pixels(baseline, output, (size_t)width * height, 2) > (size_t)width * height / 4,
        "Contrast control did not affect source details");
    defaults(program);
    glUniform4f(uniform(program, "crtStyle"), 2, 1, 0, .6f);
    glUniform4f(uniform(program, "crtOptics"), 1, 0, 1, 0);
    read_pixels(baseline, width, height);
    glUniform4f(uniform(program, "crtOptics"), 6, 0, 1, 0);
    read_pixels(output, width, height);
    require(different_pixels(baseline, output, (size_t)width * height, 2) > (size_t)width,
        "Glow radius did not change the spread around source edges");
    save_rgb(argv[3], "wide-glow", output, width, height);
    defaults(program);
    read_pixels(baseline, width, height);
    for (int type = 1; type <= 4; type++) {
        glUniform4f(uniform(program, "crtGlitch"), type, .5f, .8f, .37f);
        read_pixels(output, width, height);
        size_t changed = different_pixels(baseline, output, (size_t)width * height, 1);
        printf("Glitch type %d: %zu changed source pixels\n", type, changed);
        require(changed > (size_t)width / 4, "Glitch has no observable output");
        glUniform4f(uniform(program, "crtStyle"), 2, 0, 0, 0);
        read_pixels(output, width, height);
        require(different_pixels(input, output, (size_t)width * height, 1) == 0,
            "Zero intensity must bypass glitch displacement and color changes");
        glUniform4f(uniform(program, "crtStyle"), 2, 1, 0, 0);
        glUniform4f(uniform(program, "crtGlitch"), 0, 0, 0, 0);
        read_pixels(output, width, height);
        require(different_pixels(baseline, output, (size_t)width * height, 1) == 0,
            "Resetting a glitch did not restore the baseline image");
    }
    printf("PASS: shader compile/link, real FBO, GPU queries, passthrough, scanline spacing/width, custom color, masks, optics, all four glitches and glitch reset\n");
    glDeleteFramebuffers(1, &framebuffer); glDeleteTextures(2, textures);
    glDeleteProgram(program); glDeleteShader(shader);
    glXMakeCurrent(display, None, NULL); glXDestroyContext(display, context);
    XDestroyWindow(display, window); XFreeColormap(display, colormap); XFree(visual); XCloseDisplay(display);
    free(input); free(output); free(baseline); free(flat); free((void *)shader_source);
    return 0;
}
