Shader "Hidden/AudioTailor/Waveform"
{
    SubShader
    {
        Cull Off
        ZWrite Off
        ZTest Always

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.5
            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct v2f
            {
                float2 uv : TEXCOORD0;
                float4 vertex : SV_POSITION;
            };

            StructuredBuffer<float2> _WaveformBuffer;
            int _PixelCount;
            float4 _WaveColor;
            float4 _BackgroundColor;

            v2f vert(appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }

            float4 frag(v2f i) : SV_Target
            {
                // 3-sample horizontal multisample for antialiasing
                const int sampleCount = 3;
                float val = 0;
                for (int s = 0; s < sampleCount; s++)
                {
                    float uvx = i.uv.x + (s - 1) * (1.0 / _PixelCount) / sampleCount;
                    int x = clamp((int)(uvx * _PixelCount), 0, _PixelCount - 1);
                    float2 minMax = _WaveformBuffer[x];

                    // Map UV y [0,1] → signal range [-1,1]
                    float y = i.uv.y * 2.0 - 1.0;
                    float d = abs(y - clamp(y, minMax.x, minMax.y));

                    // Distance-based soft edge: 1 pixel fade
                    val += (1.0 - saturate(d * _PixelCount)) / sampleCount;
                }

                return lerp(_BackgroundColor, _WaveColor, sqrt(val));
            }
            ENDCG
        }
    }
}
