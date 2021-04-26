using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace UniGLTF
{
    public static class MeshExporterDivided
    {
        public class BlendShapeBuffer
        {
            readonly List<Vector3> m_positions;
            readonly List<Vector3> m_normals;

            public BlendShapeBuffer(int reserve)
            {
                m_positions = new List<Vector3>(reserve);
                m_normals = new List<Vector3>(reserve);
            }

            public void Push(Vector3 position, Vector3 normal)
            {
                m_positions.Add(position);
                m_normals.Add(normal);
            }

            public gltfMorphTarget ToGltf(glTF gltf, int bufferIndex, bool useNormal)
            {
                var positionAccessorIndex = gltf.ExtendBufferAndGetAccessorIndex(bufferIndex, m_positions.ToArray(), glBufferTarget.ARRAY_BUFFER);
                var normalAccessorIndex = -1;
                if (useNormal)
                {
                    normalAccessorIndex = gltf.ExtendBufferAndGetAccessorIndex(bufferIndex, m_normals.ToArray(), glBufferTarget.ARRAY_BUFFER);
                }
                return new gltfMorphTarget
                {
                    POSITION = positionAccessorIndex,
                    NORMAL = normalAccessorIndex,
                };
            }
        }

        public class VertexBuffer
        {
            /// <summary>
            /// SubMeshで分割するので index が変わる。対応表
            /// </summary>
            /// <typeparam name="int"></typeparam>
            /// <typeparam name="int"></typeparam>
            /// <returns></returns>
            readonly Dictionary<int, int> m_vertexIndexMap = new Dictionary<int, int>();
            public bool ContainsTriangle(int v0, int v1, int v2)
            {
                if (!m_vertexIndexMap.ContainsKey(v0))
                {
                    return false;
                }
                if (!m_vertexIndexMap.ContainsKey(v1))
                {
                    return false;
                }
                if (!m_vertexIndexMap.ContainsKey(v2))
                {
                    return false;
                }
                return true;
            }

            readonly List<Vector3> m_positions;
            readonly List<Vector3> m_normals;
            readonly List<Vector2> m_uv;

            readonly Func<int, int> m_getJointIndex;
            readonly List<UShort4> m_joints;
            readonly List<Vector4> m_weights;

            public VertexBuffer(int vertexCount, Func<int, int> getJointIndex)
            {
                m_positions = new List<Vector3>(vertexCount);
                m_normals = new List<Vector3>(vertexCount);
                m_uv = new List<Vector2>();

                m_getJointIndex = getJointIndex;
                if (m_getJointIndex != null)
                {
                    m_joints = new List<UShort4>(vertexCount);
                    m_weights = new List<Vector4>(vertexCount);
                }
            }

            public void Push(int index, Vector3 position, Vector3 normal, Vector2 uv)
            {
                var newIndex = m_positions.Count;
                m_vertexIndexMap.Add(index, newIndex);

                m_positions.Add(position);
                m_normals.Add(normal);
                m_uv.Add(uv);
            }

            public void Push(BoneWeight boneWeight)
            {
                m_joints.Add(new UShort4((ushort)boneWeight.boneIndex0, (ushort)boneWeight.boneIndex1, (ushort)boneWeight.boneIndex2, (ushort)boneWeight.boneIndex3));
                m_weights.Add(new Vector4(boneWeight.weight0, boneWeight.weight1, boneWeight.weight2, boneWeight.weight3));
            }

            public glTFPrimitives ToGltfPrimitive(glTF gltf, int bufferIndex, int materialIndex, IEnumerable<int> indices)
            {
                var indicesAccessorIndex = gltf.ExtendBufferAndGetAccessorIndex(bufferIndex, indices.Select(x => (uint)m_vertexIndexMap[x]).ToArray(), glBufferTarget.ELEMENT_ARRAY_BUFFER);
                var positionAccessorIndex = gltf.ExtendBufferAndGetAccessorIndex(bufferIndex, m_positions.ToArray(), glBufferTarget.ARRAY_BUFFER);
                var normalAccessorIndex = gltf.ExtendBufferAndGetAccessorIndex(bufferIndex, m_normals.ToArray(), glBufferTarget.ARRAY_BUFFER);
                var uvAccessorIndex0 = gltf.ExtendBufferAndGetAccessorIndex(bufferIndex, m_uv.ToArray(), glBufferTarget.ARRAY_BUFFER);

                int? jointsAccessorIndex = default;
                if (m_joints != null)
                {
                    jointsAccessorIndex = gltf.ExtendBufferAndGetAccessorIndex(bufferIndex, m_joints.ToArray(), glBufferTarget.ARRAY_BUFFER);
                }
                int? weightAccessorIndex = default;
                if (m_weights != null)
                {
                    weightAccessorIndex = gltf.ExtendBufferAndGetAccessorIndex(bufferIndex, m_weights.ToArray(), glBufferTarget.ARRAY_BUFFER);
                }

                var primitive = new glTFPrimitives
                {
                    indices = indicesAccessorIndex,
                    attributes = new glTFAttributes
                    {
                        POSITION = positionAccessorIndex,
                        NORMAL = normalAccessorIndex,
                        TEXCOORD_0 = uvAccessorIndex0,
                        JOINTS_0 = jointsAccessorIndex.GetValueOrDefault(-1),
                        WEIGHTS_0 = weightAccessorIndex.GetValueOrDefault(-1),
                    },
                    material = materialIndex,
                    mode = 4,
                };

                return primitive;
            }
        }

        public static glTFMesh Export(glTF gltf, int bufferIndex,
            MeshWithRenderer unityMesh, List<Material> unityMaterials,
            IAxisInverter axisInverter, MeshExportSettings settings)
        {
            var mesh = unityMesh.Mesh;
            var gltfMesh = new glTFMesh(mesh.name);

            if (settings.ExportTangents)
            {
                // support しない
                throw new NotImplementedException();
            }

            var positions = mesh.vertices;
            var normals = mesh.normals;
            var uv = mesh.uv;
            var boneWeights = mesh.boneWeights;

            Func<int, int> getJointIndex = null;
            if (boneWeights != null && boneWeights.Length == positions.Length)
            {
                getJointIndex = unityMesh.GetJointIndex;
            }

            Vector3[] blendShapePositions = new Vector3[mesh.vertexCount];
            Vector3[] blendShapeNormals = new Vector3[mesh.vertexCount];

            var usedIndices = new List<int>();
            for (int i = 0; i < mesh.subMeshCount; ++i)
            {
                var indices = mesh.GetIndices(i);

                // mesh
                // index の順に attributes を蓄える                
                var buffer = new VertexBuffer(indices.Length, getJointIndex);
                // indices から参照される頂点だけを蓄える
                usedIndices.Clear();
                for (int k = 0; k < positions.Length; ++k)
                {
                    if (indices.Contains(k))
                    {
                        usedIndices.Add(k);
                        buffer.Push(k, axisInverter.InvertVector3(positions[k]), axisInverter.InvertVector3(normals[k]), uv[k].ReverseUV());
                        if (getJointIndex != null)
                        {
                            buffer.Push(boneWeights[k]);
                        }
                    }
                }

                var material = unityMesh.Renderer.sharedMaterials[i];
                var materialIndex = -1;
                if (material != null)
                {
                    materialIndex = unityMaterials.IndexOf(material);
                }
                var indexMap = usedIndices.Select((used, index) => (used, index)).ToDictionary(x => x.used, x => x.index);

                var flipped = new List<int>();
                for (int j = 0; j < indices.Length; j += 3)
                {
                    if (buffer.ContainsTriangle(j, j + 1, j + 2))
                    {
                        flipped.Add(indexMap[indices[j + 2]]);
                        flipped.Add(indexMap[indices[j + 1]]);
                        flipped.Add(indexMap[indices[j]]);
                    }
                    else
                    {
                        Debug.LogWarning($"triangle not contains [{j}, {j + 1}, {j + 2}]");
                    }
                }
                var gltfPrimitive = buffer.ToGltfPrimitive(gltf, bufferIndex, materialIndex, flipped);

                // blendShape
                for (int j = 0; j < mesh.blendShapeCount; ++j)
                {
                    var blendShape = new BlendShapeBuffer(indices.Length);

                    // index の順に attributes を蓄える                
                    mesh.GetBlendShapeFrameVertices(j, 0, blendShapePositions, blendShapeNormals, null);
                    foreach (var k in usedIndices)
                    {
                        blendShape.Push(
                            axisInverter.InvertVector3(blendShapePositions[k]),
                            axisInverter.InvertVector3(blendShapeNormals[k]));
                    }

                    gltfPrimitive.targets.Add(blendShape.ToGltf(gltf, bufferIndex, !settings.ExportOnlyBlendShapePosition));
                }

                gltfMesh.primitives.Add(gltfPrimitive);
            }

            var targetNames = Enumerable.Range(0, mesh.blendShapeCount).Select(x => mesh.GetBlendShapeName(x)).ToArray();
            gltf_mesh_extras_targetNames.Serialize(gltfMesh, targetNames);

            return gltfMesh;
        }
    }
}
