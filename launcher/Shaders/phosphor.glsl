#version 330
// Converts the menu image to linear light and keeps a decaying copy of earlier frames (phosphor afterglow).
in vec2 uv;
out vec4 o;
uniform sampler2D src;
uniform sampler2D prev;
uniform float decay;
uniform float gamma;
void main()
{
    vec3 c = pow(texture(src, uv).rgb, vec3(gamma));
    vec3 p = texture(prev, uv).rgb * decay;
    o = vec4(max(c, p), 1.0);
}
