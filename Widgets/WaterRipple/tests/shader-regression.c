// SPDX-License-Identifier: GPL-3.0-or-later
// Validate the production shader on actual GPU framebuffer attachments.
#define GL_GLEXT_PROTOTYPES
#include <GL/gl.h>
#include <GL/glext.h>
#include <GL/glx.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#define SIZE 512
#define BYTES (SIZE * SIZE * 4)
static void check(int condition, const char *reason) {
    if (!condition) { fprintf(stderr, "FAIL: %s\n", reason); exit(1); }
}
static unsigned char *read_all(const char *path, size_t *length) {
    FILE *file = fopen(path, "rb"); check(file != NULL, "open input");
    check(!fseek(file, 0, SEEK_END), "seek input");
    long size = ftell(file); check(size > 0, "empty input"); rewind(file);
    unsigned char *text = calloc((size_t)size + 1, 1); check(text != NULL, "allocate input");
    check(fread(text, 1, (size_t)size, file) == (size_t)size, "read input");
    fclose(file); if (length) *length = (size_t)size; return text;
}
static GLuint compile_program(const char *path) {
    char *text = (char *)read_all(path, NULL);
    GLuint shader = glCreateShader(GL_FRAGMENT_SHADER);
    const char *source = text; glShaderSource(shader, 1, &source, NULL); glCompileShader(shader);
    GLint success; glGetShaderiv(shader, GL_COMPILE_STATUS, &success);
    if (!success) { char log[8192]; glGetShaderInfoLog(shader, sizeof(log), NULL, log); fprintf(stderr, "%s\n", log); }
    check(success, "shader compile");
    GLuint program = glCreateProgram(); glAttachShader(program, shader); glLinkProgram(program);
    glGetProgramiv(program, GL_LINK_STATUS, &success);
    if (!success) { char log[8192]; glGetProgramInfoLog(program, sizeof(log), NULL, log); fprintf(stderr, "%s\n", log); }
    check(success, "shader link"); glDeleteShader(shader); free(text); return program;
}
typedef struct {
    float strength, speed, wavelength, rings, lifetime, damping, light, radius;
    float aspect, invert, separation, blend, crest, secondary, liquid, age;
} Values;
static Values defaults(void) {
    return (Values){7,190,32,3,3.2f,1.2f,.03f,8,1,0,.18f,.45f,.5f,.5f,.4f,.55f};
}
static void uniforms(GLuint program, Values p) {
    glUseProgram(program);
    glUniform1i(glGetUniformLocation(program,"rippleTexture"),0);
    glUniform2f(glGetUniformLocation(program,"rippleSize"),SIZE,SIZE);
    glUniform2f(glGetUniformLocation(program,"rippleCenter"),SIZE/2,SIZE/2);
    glUniform4f(glGetUniformLocation(program,"rippleShape"),p.strength,p.speed,p.wavelength,p.rings);
    glUniform4f(glGetUniformLocation(program,"rippleFade"),p.lifetime,p.damping,p.light,p.radius);
    glUniform2f(glGetUniformLocation(program,"rippleExtra"),p.aspect,p.invert);
    glUniform4f(glGetUniformLocation(program,"rippleFilters"),p.separation,p.blend,p.crest,p.secondary);
    glUniform1f(glGetUniformLocation(program,"rippleLiquid"),p.liquid);
    glUniform1f(glGetUniformLocation(program,"rippleAge"),p.age);
}
static void paint(void) {
    glBegin(GL_QUADS);
    glTexCoord2f(0,0); glVertex2f(-1,-1); glTexCoord2f(1,0); glVertex2f(1,-1);
    glTexCoord2f(1,1); glVertex2f(1,1); glTexCoord2f(0,1); glVertex2f(-1,1);
    glEnd();
}
static void render(GLuint program, Values p, unsigned char *image) {
    uniforms(program,p); paint(); glReadPixels(0,0,SIZE,SIZE,GL_RGBA,GL_UNSIGNED_BYTE,image);
    check(glGetError()==GL_NO_ERROR,"GPU readback error");
}
static size_t changed(const unsigned char *a,const unsigned char *b,int tolerance) {
    size_t count=0;
    for(size_t i=0;i<BYTES;i+=4) {
        if(abs(a[i]-b[i])>tolerance || abs(a[i+1]-b[i+1])>tolerance || abs(a[i+2]-b[i+2])>tolerance) count++;
    }
    return count;
}
static void save(const char *directory,const char *name,const unsigned char *image) {
    char path[4096]; check(snprintf(path,sizeof(path),"%s/%s.rgba",directory,name)<(int)sizeof(path),"path length");
    FILE *file=fopen(path,"wb");check(file!=NULL,"open output");
    check(fwrite(image,1,BYTES,file)==BYTES,"write output");fclose(file);
}
static double benchmark(GLuint program,Values p) {
    uniforms(program,p); for(int i=0;i<12;i++)paint();glFinish();
    GLuint query;glGenQueries(1,&query);glBeginQuery(GL_TIME_ELAPSED,query);
    for(int i=0;i<90;i++)paint();
    glEndQuery(GL_TIME_ELAPSED);
    GLuint64 ns;glGetQueryObjectui64v(query,GL_QUERY_RESULT,&ns);glDeleteQueries(1,&query);
    check(ns>0,"GPU timer unsupported");return (double)ns/90.0/1e6;
}
int main(int argc,char **argv) {
    setvbuf(stdout,NULL,_IOLBF,0);
    check(argc==5,"usage: shader-regression production.frag baseline.frag source.rgba output-dir");
    Display *display=XOpenDisplay(NULL);check(display!=NULL,"X display unavailable");
    int attributes[]={GLX_RGBA,None};XVisualInfo *visual=glXChooseVisual(display,DefaultScreen(display),attributes);check(visual!=NULL,"GLX visual");
    GLXContext context=glXCreateContext(display,visual,NULL,True);check(context!=NULL,"GLX context");
    Colormap map=XCreateColormap(display,RootWindow(display,visual->screen),visual->visual,AllocNone);
    XSetWindowAttributes attrs={.colormap=map};
    Window window=XCreateWindow(display,RootWindow(display,visual->screen),0,0,1,1,0,visual->depth,InputOutput,visual->visual,CWColormap,&attrs);
    check(glXMakeCurrent(display,window,context),"activate GLX context");
    printf("Renderer: %s\nOpenGL: %s\n",glGetString(GL_RENDERER),glGetString(GL_VERSION));
    GLuint production=compile_program(argv[1]),baseline=compile_program(argv[2]);
    size_t input_size;
    unsigned char *input=read_all(argv[3], &input_size);
    check(input_size==BYTES,"source must be exactly512x512RGBA");
    unsigned char *a=malloc(BYTES),*b=malloc(BYTES),*c=malloc(BYTES);check(a&&b&&c,"image buffers");
    GLuint textures[2];glGenTextures(2,textures);
    for(int i=0;i<2;i++) {
        glBindTexture(GL_TEXTURE_2D,textures[i]);
        glTexParameteri(GL_TEXTURE_2D,GL_TEXTURE_MIN_FILTER,GL_LINEAR);glTexParameteri(GL_TEXTURE_2D,GL_TEXTURE_MAG_FILTER,GL_LINEAR);
        glTexParameteri(GL_TEXTURE_2D,GL_TEXTURE_WRAP_S,GL_CLAMP_TO_EDGE);glTexParameteri(GL_TEXTURE_2D,GL_TEXTURE_WRAP_T,GL_CLAMP_TO_EDGE);
        glTexImage2D(GL_TEXTURE_2D,0,GL_RGBA8,SIZE,SIZE,0,GL_RGBA,GL_UNSIGNED_BYTE,i?NULL:input);
    }
    GLuint framebuffer;glGenFramebuffers(1,&framebuffer);glBindFramebuffer(GL_FRAMEBUFFER,framebuffer);
    glFramebufferTexture2D(GL_FRAMEBUFFER,GL_COLOR_ATTACHMENT0,GL_TEXTURE_2D,textures[1],0);
    check(glCheckFramebufferStatus(GL_FRAMEBUFFER)==GL_FRAMEBUFFER_COMPLETE,"real framebuffer attachment");
    glDrawBuffer(GL_COLOR_ATTACHMENT0);glReadBuffer(GL_COLOR_ATTACHMENT0);glBindTexture(GL_TEXTURE_2D,textures[0]);
    glViewport(0,0,SIZE,SIZE);glDisable(GL_DITHER);glDisable(GL_BLEND);glColor4f(1,1,1,1);
    Values p=defaults();
    render(baseline,p,a);render(production,p,b);
    printf("Classic parity: %zu pixels differ\n",changed(a,b,0));
    check(changed(a,b,0)==0,"classic effect changed");
    save(argv[4],"classic",b);
    p.wavelength=104;render(production,p,b);save(argv[4],"classic-wide",b);
    check(changed(a,b,1)>1000,"ripple ignores chosen ring spacing");
    const char *names[]={"strength","speed","wavelength","rings","lifetime","damping","light","radius","aspect","invert","separation","blend","crest","secondary","liquid"};
    // Each comparison changes exactly one requested control on the same rendered fixture.
    for(int i=0;i<15;i++) {
        p=defaults();
        switch(i){case 0:p.strength=3;break;case 1:p.speed=90;break;case 2:p.wavelength=18;break;
        case 3:p.rings=1;break;case 4:p.lifetime=.9f;break;case 5:p.damping=2.8f;break;
        case 6:p.light=.15f;break;case 7:p.radius=50;break;case 8:p.aspect=.65f;break;
        case 9:p.invert=1;break;case 10:p.separation=0;break;case 11:p.blend=0;break;
        case 12:p.crest=0;break;case 13:p.secondary=0;break;case 14:p.liquid=0;break;}
        render(production,p,b);size_t count=changed(a,b,1);
        printf("Control %s: %zu changed pixels\n",names[i],count);
        check(count>30,"ripple ignored a pipeline control");
        render(baseline,p,c);
        check(changed(b,c,0)==0,"classic parity changed for a pipeline control");
    }
    p=defaults();p.age=0;render(production,p,b);
    check(changed(input,b,1)==0,"zero-age wave must preserve input");
    p=defaults();p.strength=0;p.light=0;p.crest=0;render(production,p,b);
    check(changed(input,b,1)==0,"zero displacement and lighting must preserve input");
    p=defaults();p.separation=0;render(production,p,a);
    p.blend=0;render(production,p,b);
    check(changed(a,b,1)==0,"disabled chromatic separation must ignore blend");
    for(size_t i=0;i<BYTES;i+=4)c[i]=c[i+1]=c[i+2]=input[i],c[i+3]=255;
    glTexSubImage2D(GL_TEXTURE_2D,0,0,0,SIZE,SIZE,GL_RGBA,GL_UNSIGNED_BYTE,c);
    p=defaults();p.separation=0;p.light=0;p.crest=0;render(production,p,b);
    size_t chroma=0;
    for(size_t i=0;i<BYTES;i+=4)if(abs(b[i]-b[i+1])>1||abs(b[i+1]-b[i+2])>1)chroma++;
    check(chroma==0,"ripple added colored fringes despite chromatic and lighting filters being disabled");
    glTexSubImage2D(GL_TEXTURE_2D,0,0,0,SIZE,SIZE,GL_RGBA,GL_UNSIGNED_BYTE,input);
    printf("512x512 real RGBA8 FBO, 12 warmup + 90 timed draws. GPU shader time ONLY; excludes composition and presentation.\n");
    p=defaults();printf("Classic spacing32: %.5f ms/draw\n",benchmark(production,p));
    p.wavelength=104;printf("Classic spacing104: %.5f ms/draw\n",benchmark(production,p));
    printf("PASS exact classic parity, all pipeline controls, spacing, no forced chromatic, zero-age and zero-displacement bypass\n");
    glDeleteFramebuffers(1,&framebuffer);glDeleteTextures(2,textures);glDeleteProgram(production);glDeleteProgram(baseline);
    glXMakeCurrent(display,None,NULL);glXDestroyContext(display,context);XDestroyWindow(display,window);XFreeColormap(display,map);XFree(visual);XCloseDisplay(display);
    free(input);free(a);free(b);free(c);return 0;
}
