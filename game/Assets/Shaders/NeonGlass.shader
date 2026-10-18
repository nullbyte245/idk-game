// Frosted edge-lit pane. Built on the Standard model rather than as an unlit cutout so the pane
// picks up the reflection probe: a mirror-smooth surface with a low alpha is what makes glass read
// as glass instead of as a coloured film.
Shader "NeonRelay/Neon Glass"
{
    Properties
    {
        _Color ("Tint", Color) = (0.30, 0.72, 1.0, 0.16)
        [HDR] _Glow ("Edge Glow", Color) = (0.28, 0.86, 1.0, 1)
        _Edge ("Edge Strength", Range(0, 4)) = 1.5
        _Metallic ("Metallic", Range(0, 1)) = 0.1
        _Smoothness ("Smoothness", Range(0, 1)) = 0.95
    }

    SubShader
    {
        Tags
        {
            "Queue" = "Transparent"
            "RenderType" = "Transparent"
            "IgnoreProjector" = "True"
        }

        CGPROGRAM
        #pragma surface surf Standard alpha:blend
        #pragma target 3.0

        fixed4 _Color;
        fixed4 _Glow;
        float _Edge;
        half _Metallic;
        half _Smoothness;

        struct Input
        {
            float3 viewDir;
        };

        void surf (Input IN, inout SurfaceOutputStandard o)
        {
            float facing = saturate(dot(normalize(IN.viewDir), float3(0, 0, 1)));
            float rim = pow(1.0 - facing, 2.2);

            o.Albedo = _Color.rgb;
            o.Metallic = _Metallic;
            o.Smoothness = _Smoothness;
            o.Emission = _Glow.rgb * _Edge * rim;
            // Face-on the pane is nearly invisible; grazing angles have to stack up into a solid
            // edge, otherwise the silhouette disappears against the sky.
            o.Alpha = saturate(_Color.a + rim * 0.8);
        }
        ENDCG
    }

    Fallback "Transparent/Diffuse"
}
