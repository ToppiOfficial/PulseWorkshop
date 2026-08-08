// Mesh + grid fragment shader for the Unpack model preview.
//
// The material's diffuse map (VertexLitGeneric/UnlitGeneric $basetexture, an eye shader's $iris)
// times a fixed three-term studio light rig - key, fill, ambient - in view-independent world space.
// pc.color.a mixes the shading out, which is what makes UnlitGeneric and the grid lines flat.
//
// Compiled to mesh.frag.spv and embedded - see Shaders/README.md to regenerate.
#version 450

layout(push_constant) uniform Push {
    mat4 mvp;
    vec4 color;  // rgb = tint, a = $alpha opacity (1 for anything opaque)
    vec4 key;    // xyz = direction from the surface to the key light, w = $alphatest cutoff
    vec4 params; // x = how much diffuse shading to apply (0 = flat)
} pc;

layout(set = 0, binding = 0) uniform sampler2D diffuse;

layout(location = 0) in vec3 vNormal;
layout(location = 1) in vec2 vUv;

layout(location = 0) out vec4 outColor;

void main() {
    vec4 texel = texture(diffuse, vUv);
    // $alphatest. The cutoff is 0 for everything else, so this never fires on an opaque material.
    if (texel.a < pc.key.w) discard;

    vec3 n = normalize(vNormal);

    // The key rides with the camera (the app swings it off the eye vector), so orbiting can never
    // park the viewer on an unlit side. A world-fixed rig cannot: which way a model's front points
    // is a property of how it was compiled, not something the shader can know.
    vec3 k = normalize(pc.key.xyz);
    // Fill comes back from the opposite azimuth and low down, standing in for bounce.
    vec3 f = normalize(vec3(-k.x, -k.y, 0.30));

    float key  = max(dot(n, k), 0.0);
    float fill = max(dot(n, f), 0.0);
    // Ambient is deliberately generous: a preview is read for silhouette and surface detail, and a
    // dark side-facing panel hides both.
    float lit  = 0.44 + 0.52 * key + 0.20 * fill;
    // Alpha only matters on the $translucent pipeline; the opaque one has blending disabled.
    outColor = vec4(pc.color.rgb * texel.rgb * mix(1.0, lit, pc.params.x), texel.a * pc.color.a);
}
