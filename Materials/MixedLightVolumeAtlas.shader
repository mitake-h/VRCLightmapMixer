Shader "Custom/Blend3D_CRT"
{
    Properties
    {
        _TexA("Texture A", 3D) = "white" {}
        _TexB("Texture B", 3D) = "black" {}
        _WeightA("Weight A", Range(0,1)) = 1.0
        _WeightB("Weight B", Range(0,1)) = 0.0
    }

        SubShader
        {
            Tags { "RenderType" = "CustomRenderTexture" }
            Cull Off ZWrite Off ZTest Always

            Pass
            {
                Name "Blend3D"
                CGPROGRAM
                #pragma vertex   CustomRenderTextureVertexShader
                #pragma fragment frag
                #include "UnityCustomRenderTexture.cginc"
                #include "UnityCG.cginc"

                sampler3D _TexA;
                sampler3D _TexB;
                float     _WeightA;
                float     _WeightB;

                fixed4 frag(v2f_customrendertexture IN) : SV_Target
                {
                    float3 uvw = IN.localTexcoord.xyz;
                    fixed4 a = tex3D(_TexA, uvw);
                    fixed4 b = tex3D(_TexB, uvw);
                    return a * _WeightA + b * _WeightB;
                }
                ENDCG
            }
        }
            Fallback Off
}
