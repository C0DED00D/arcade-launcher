#version 330
// 9-tap Gaussian (5 linear-filtered fetches) in one direction; used for halation and wide glow.
in vec2 uv;
out vec4 o;
uniform sampler2D src;
uniform vec2 dir; // one source texel in uv units, times the spread
void main()
{
    vec3 s = texture(src, uv).rgb * 0.2270270270;
    s += (texture(src, uv + dir * 1.3846153846).rgb + texture(src, uv - dir * 1.3846153846).rgb) * 0.3162162162;
    s += (texture(src, uv + dir * 3.2307692308).rgb + texture(src, uv - dir * 3.2307692308).rgb) * 0.0702702703;
    o = vec4(s, 1.0);
}
