#version 430
// Arcade launcher CRT: separable gaussian blur (direction in sDir, in texels of this pass's target).
uniform vec4 sRTSize;
uniform float sDirX;

layout(binding = 0) uniform sampler2D sTex;
layout(location = 0) in vec2 ilUV;
layout(location = 0) out vec4 outColor0;

void main()
{
	vec2 step = (sDirX > 0.5) ? vec2(sRTSize.z, 0.0) : vec2(0.0, sRTSize.w);
	// 9 taps using linear filtering between texel pairs
	const float o[5] = float[5](0.0, 1.3846153846, 3.2307692308, 5.0769230769, 6.9230769231);
	const float w[5] = float[5](0.2270270270, 0.3162162162, 0.0702702703, 0.0108108108, 0.0010810811);
	vec3 s = texture(sTex, ilUV).rgb * w[0];
	for (int i = 1; i < 5; i++)
	{
		s += texture(sTex, ilUV + step * o[i] * 1.6).rgb * w[i];
		s += texture(sTex, ilUV - step * o[i] * 1.6).rgb * w[i];
	}
	outColor0 = vec4(s, 1.0);
}
