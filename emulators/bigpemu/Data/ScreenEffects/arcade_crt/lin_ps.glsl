#version 430
// Arcade launcher CRT, pass 1: Jaguar picture to linear light.
layout(binding = 0) uniform sampler2D sTex;
layout(location = 0) in vec2 ilUV;
layout(location = 0) out vec4 outColor0;

void main()
{
	vec3 c = texture(sTex, ilUV).rgb;
	outColor0 = vec4(pow(c, vec3(2.2)), 1.0);
}
