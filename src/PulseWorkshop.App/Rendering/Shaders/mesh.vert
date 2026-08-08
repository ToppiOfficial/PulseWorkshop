// Mesh + grid vertex shader for the Unpack model preview.
//
// One shader pair covers both draws: the mesh (triangle list, lit, textured) and the ground grid
// (line list, flat, sampling a 1x1 white texture so the same pipeline layout serves both).
//
// Compiled to mesh.vert.spv and embedded - see Shaders/README.md to regenerate.
#version 450

layout(push_constant) uniform Push {
    mat4 mvp;
    vec4 color;  // fragment stage; declared here so both blocks match
    vec4 key;
    vec4 params;
} pc;

layout(location = 0) in vec3 inPos;
layout(location = 1) in vec3 inNormal;
layout(location = 2) in vec2 inUv;

layout(location = 0) out vec3 vNormal;
layout(location = 1) out vec2 vUv;

void main() {
    gl_Position = pc.mvp * vec4(inPos, 1.0);
    vNormal = inNormal;
    vUv = inUv;
}
