﻿using PD2ModelParser.Sections;
using SharpGLTF.Memory;
using System;
using System.Collections.Generic;
using System.Linq;
using GLTF = SharpGLTF.Schema2;
using System.Numerics;

namespace PD2ModelParser.Exporters
{
    class GltfExporter
    {
        public static string ExportFile(FullModelData data, string path)
        {
            var exporter = new GltfExporter();
            var gltfmodel = exporter.Convert(data);
            gltfmodel.SaveGLTF(path, Newtonsoft.Json.Formatting.Indented);

            return path;
        }

        FullModelData data;
        GLTF.ModelRoot root;
        GLTF.Scene scene;
        Dictionary<uint, List<(string, GLTF.Accessor)>> vertexAttributesByGeometryId;
        Dictionary<uint, GLTF.Material> materialsBySectionId;
        Dictionary<uint, GLTF.Node> nodesBySectionId;
        List<(Model, GLTF.Node)> toSkin;

        /// <summary>
        /// How much to embiggen data as it's converted to GLTF.
        /// </summary>
        /// <remarks>
        /// See the remarks of <see cref="PD2ModelParser.Importers.GltfImporter.scaleFactor"/> 
        /// for why the implementation here works.
        /// </remarks>
        float scaleFactor = 0.01f;

        GLTF.ModelRoot Convert(FullModelData data)
        {
            vertexAttributesByGeometryId = new Dictionary<uint, List<(string, GLTF.Accessor)>>();
            materialsBySectionId = new Dictionary<uint, GLTF.Material>();
            nodesBySectionId = new Dictionary<uint, GLTF.Node>();
            toSkin = new List<(Model, GLTF.Node)>();

            this.data = data;
            root = GLTF.ModelRoot.CreateModel();
            scene = root.UseScene(0);

            foreach (var ms in data.parsed_sections.Where(i => i.Value is Material).Select(i => i.Value as Material))
            {
                materialsBySectionId[ms.SectionId] = root.CreateMaterial(ms.hashname.String);
            }

            foreach(var i in data.SectionsOfType<Object3D>().Where(i => i.parent == null))
            {
                CreateNodeFromObject3D(i, scene);
            }

            foreach(var (thing, node) in toSkin)
            {
                SkinModel(thing, node);
                //node.LocalMatrix = Matrix4x4.Identity;
                //node.Skin = GetSkinForModel(thing);
            }

            //root.MergeBuffers();

            return root;
        }

        void CreateNodeFromObject3D(Object3D thing, GLTF.IVisualNodeContainer parent)
        {
            var isSkinned = thing is Model m && m.skinbones_ID != 0;
            if (isSkinned)
            {
                parent = scene;
            }

            var node = parent.CreateNode(thing.Name);

            nodesBySectionId[thing.SectionId] = node;
            if (thing != null)
            {
                var istrs = thing.rotation.Decompose(out var scale, out var rotation, out var translation);
                if(!istrs)
                {
                    throw new Exception($"In object \"{thing.Name}\" ({thing.SectionId}), non-TRS matrix");
                }

                // We only did that to be sure it was a TRS matrix. Knowing it is, and knowing we only
                // want to affect the translation, less stability problems exist by directly changing
                // just the cells that are the translation part.

                var mat = thing.rotation.ToMatrix4x4();
                mat.Translation = mat.Translation * scaleFactor;

                node.LocalMatrix = isSkinned ? Matrix4x4.Identity : mat;
                
            }
            if (thing is Model)
            {
                node.Mesh = GetMeshForModel(thing as Model);
                if ((thing as Model).skinbones_ID != 0)
                {
                    toSkin.Add((thing as Model, node));
                }
            }
            foreach (var i in thing.children)
            {
                CreateNodeFromObject3D(i, node);
            }
        }

        void CreateModelNode(Model model, GLTF.IVisualNodeContainer parent)
        {
            var mesh = GetMeshForModel(model);
            GLTF.Node node;
        }

        void SkinModel(Model model, GLTF.Node node)
        {
            if (!data.parsed_sections.ContainsKey(model.skinbones_ID))
            {
                return;
            }

            var skinbones = data.parsed_sections[model.skinbones_ID] as SkinBones;
            var skin = root.CreateSkin(model.Name + "_Skin");
            skin.Skeleton = nodesBySectionId[skinbones.probably_root_bone];

            var wt = node.WorldMatrix;
            node.LocalTransform = Matrix4x4.Identity;

            skin.BindJoints(wt, skinbones.bone_mappings[0].bones.Select(i => nodesBySectionId[skinbones.objects[(int)i]]).ToArray());
            node.Skin = skin;
        }

        GLTF.Mesh GetMeshForModel(Model model)
        {
            if(!data.parsed_sections.ContainsKey(model.passthroughGP_ID))
            {
                return null;
            }

            var mesh = root.CreateMesh(model.Name);

            var secPassthrough = (PassthroughGP)data.parsed_sections[model.passthroughGP_ID];
            var geometry = (Geometry)data.parsed_sections[secPassthrough.geometry_section];
            var topology = (Topology)data.parsed_sections[secPassthrough.topology_section];
            var materialGroup = (Material_Group)data.parsed_sections[model.material_group_section_id];

            var attribs = GetGeometryAttributes(geometry);

            foreach (var (indexAccessor,material) in CreatePrimitiveIndices(topology, model.RenderAtoms, materialGroup))
            {
                var prim = mesh.CreatePrimitive();
                prim.DrawPrimitiveType = GLTF.PrimitiveType.TRIANGLES;
                foreach (var att in attribs)
                {
                    prim.SetVertexAccessor(att.Item1, att.Item2);
                }

                prim.SetIndexAccessor(indexAccessor);
                if (material.Name != "Material: Default Material")
                {
                    prim.Material = material;
                }
            }

            return mesh;
        }

        IEnumerable<(GLTF.Accessor, GLTF.Material)> CreatePrimitiveIndices(Topology topo, IEnumerable<RenderAtom> atoms, Material_Group materialGroup)
        {
            var buf = new ArraySegment<byte>(new byte[topo.facelist.Count * 3 * 2]);
            var mai = new MemoryAccessInfo($"indices_{topo.hashname}", 0, topo.facelist.Count * 3, 0, GLTF.DimensionType.SCALAR, GLTF.EncodingType.UNSIGNED_SHORT);
            var ma = new MemoryAccessor(buf, mai);
            var array = ma.AsIntegerArray();
            for (int i = 0; i < topo.facelist.Count; i++)
            {
                array[i * 3 + 0] = topo.facelist[i].a;
                array[i * 3 + 1] = topo.facelist[i].b;
                array[i * 3 + 2] = topo.facelist[i].c;
            }

            var atomcount = 0;

            foreach (var ra in atoms)
            {
                var atom_mai = new MemoryAccessInfo($"indices_{topo.hashname}_{atomcount++}", (int)ra.BaseIndex*2, (int)ra.TriangleCount*3, 0, GLTF.DimensionType.SCALAR, GLTF.EncodingType.UNSIGNED_SHORT);
                var atom_ma = new MemoryAccessor(buf, atom_mai);
                var accessor = root.CreateAccessor();
                accessor.SetIndexData(atom_ma);
                var material = materialsBySectionId[materialGroup.items[(int)ra.MaterialId]];
                yield return (accessor, material);
            }
        }

        List<(string, GLTF.Accessor)> GetGeometryAttributes(Geometry geometry)
        {
            List<(string, GLTF.Accessor)> result;
            if(vertexAttributesByGeometryId.TryGetValue(geometry.SectionId, out result))
            {
                return result;
            }
            result = new List<(string, GLTF.Accessor)>();

            var a_pos = MakeVertexAttributeAccessor("vpos", geometry.verts.Select(i=>i*scaleFactor).ToList(), 12, GLTF.DimensionType.VEC3, MathUtil.ToVector3, ma => ma.AsVector3Array());
            result.Add(("POSITION", a_pos));

            if (geometry.normals.Count > 0)
            {
                var a_norm = MakeVertexAttributeAccessor("vnorm", geometry.normals, 12, GLTF.DimensionType.VEC3, MathUtil.ToVector3, ma => ma.AsVector3Array());
                result.Add(("NORMAL", a_norm));
            }

            if (geometry.tangents.Count > 0)
            {
                Func<Nexus.Vector3D, int, Vector4> makeTangent = (input, index) =>
                {
                    var tangent = input.ToVector3();
                    var binorm = geometry.binormals[index].ToVector3();
                    var normal = geometry.normals[index].ToVector3();

                    var txn = Vector3.Cross(tangent, normal);
                    var dot = Vector3.Dot(txn, binorm);
                    var sgn = Math.Sign(dot);

                    // A few models have vertices where tangent==binorm, which is silly
                    // also breaks because SharpGLTF tries to do validation. So we return
                    // 1 in that case, which is probably also unhelpful. I'm not 100% sure
                    // how important having sane binormals is anyway.
                    return new Vector4(tangent, sgn != 0 ? sgn : 1);
                };

                var a_binorm = MakeVertexAttributeAccessor("vtan", geometry.tangents, 16, GLTF.DimensionType.VEC4, makeTangent, ma => ma.AsVector4Array());
                result.Add(("TANGENT", a_binorm));
            }

            if (geometry.vertex_colors.Count > 0)
            {
                var a_col = MakeVertexAttributeAccessor("vcol", geometry.vertex_colors, 16, GLTF.DimensionType.VEC4, MathUtil.ToVector4, ma => ma.AsVector4Array());
                result.Add(("COLOR_0", a_col));
            }

            for (var i = 0; i < geometry.UVs.Length; i++)
            {
                var uvs = geometry.UVs[i];
                if(uvs.Count > 0)
                {
                    var a_uv = MakeVertexAttributeAccessor($"vuv_{i}", uvs, 12, GLTF.DimensionType.VEC2, FixupUV, ma => ma.AsVector2Array());
                    result.Add(($"TEXCOORD_{i}", a_uv));
                }
            }

            if (geometry.weights.Count > 0)
            {
                Func<Nexus.Vector3D, Vector4> ConvertWeight = (weight) => {
                    var n = new Vector4(weight.X, weight.Y, weight.Z, 0);
                    if((n.W < 0))
                    {
                        int a = 0;
                    }
                    return n;
                };

                var a_wght = MakeVertexAttributeAccessor("vweight", geometry.weights, 16, GLTF.DimensionType.VEC4, ConvertWeight, ma => ma.AsVector4Array());
                result.Add(("WEIGHTS_0", a_wght));
            }
            
            if(geometry.weight_groups.Count > 0)
            {
                // TODO: Is there a way that doesn't require round-tripping through float? It's unnecessary,
                // even if it doesn't actually hurt as such.
                Func<GeometryWeightGroups, Vector4> ConvertWeightGroup = (i)
                    => new Vector4(i.Bones1, i.Bones2, i.Bones3, i.Bones4);
                var a_joint = MakeVertexAttributeAccessor("vjoint", geometry.weight_groups, 8, GLTF.DimensionType.VEC4, ConvertWeightGroup, ma => ma.AsVector4Array(), GLTF.EncodingType.UNSIGNED_SHORT);
                result.Add(("JOINTS_0", a_joint));
            }

            return result;
        }

        Vector2 FixupUV(Nexus.Vector2D input) => new Vector2(input.X, 1-input.Y);

        GLTF.Accessor MakeIndexAccessor(Topology topo)
        {
            var mai = new MemoryAccessInfo($"indices_{topo.hashname}", 0, topo.facelist.Count * 3, 0, GLTF.DimensionType.SCALAR, GLTF.EncodingType.UNSIGNED_SHORT);
            var ma = new MemoryAccessor(new ArraySegment<byte>(new byte[topo.facelist.Count * 3 * 2]), mai);
            var array = ma.AsIntegerArray();
            for(int i = 0; i < topo.facelist.Count; i++)
            {
                array[i * 3 + 0] = topo.facelist[i].a;
                array[i * 3 + 1] = topo.facelist[i].b;
                array[i * 3 + 2] = topo.facelist[i].c;
            }
            var accessor = root.CreateAccessor();
            accessor.SetIndexData(ma);
            return accessor;
        }

        GLTF.Accessor MakeVertexAttributeAccessor<TSource, TResult>(string maiName, IList<TSource> source, int stride, GLTF.DimensionType dimtype, Func<TSource, TResult> conv, Func<MemoryAccessor, IList<TResult>> getcontainer, GLTF.EncodingType enc = GLTF.EncodingType.FLOAT, bool normalized = false)
        {
            return MakeVertexAttributeAccessor(maiName, source, stride, dimtype, (s, i) => conv(s), getcontainer, enc, normalized);
        }

        GLTF.Accessor MakeVertexAttributeAccessor<TSource, TResult>(string maiName, IList<TSource> source, int stride, GLTF.DimensionType dimtype, Func<TSource, int, TResult> conv, Func<MemoryAccessor, IList<TResult>> getcontainer, GLTF.EncodingType enc = GLTF.EncodingType.FLOAT, bool normalized = false)
        {
            var mai = new MemoryAccessInfo(maiName, 0, source.Count, stride, dimtype, enc, normalized);
            var ma = new MemoryAccessor(new ArraySegment<byte>(new byte[source.Count * stride]), mai);
            var array = getcontainer(ma);
            for(int i = 0; i < source.Count; i++)
            {
                array[i] = conv(source[i], i);
            }
            var accessor = root.CreateAccessor();
            accessor.SetVertexData(ma);
            return accessor;
        }
    }
}
