using System;
using Unity.Mathematics;

namespace UnityEngine.Rendering.HighDefinition
{
    public static class WorldOrogenBaseMapBinding
    {
        public static void Bind(MaterialPropertyBlock properties,Texture2DArray colour,Quaternion rotation,double3 anchor,double radius)
        {
            if(properties==null)throw new ArgumentNullException(nameof(properties));
            properties.SetFloat("_OrogenBaseColourEnabled",colour?1:0);
            if(!colour)return;
            properties.SetTexture("_OrogenBaseColour",colour);
            properties.SetVector("_OrogenBaseGrid",new Vector4(colour.width-1,colour.width,0,0));
            properties.SetMatrix("_OrogenRenderToLocal",Matrix4x4.Rotate(Quaternion.Inverse(rotation)));
            properties.SetVector("_OrogenBaseAnchor",(Vector3)(float3)(anchor/radius));properties.SetFloat("_OrogenInverseRadius",(float)(1/radius));
        }
    }
}
