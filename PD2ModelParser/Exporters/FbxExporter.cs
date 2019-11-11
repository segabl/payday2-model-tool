using System;
using System.Collections.Generic;
using FbxNet;
using Nexus;
using PD2ModelParser.Misc;
using PD2ModelParser.Sections;

namespace PD2ModelParser.Exporters
{
    static class FbxExporter
    {
        private static FbxManager fm;

        public static string ExportFile(FullModelData data, string path)
        {
            path = path.Replace(".model", ".fbx");

            if (fm == null)
            {
                fm = FbxManager.Create();

                FbxIOSettings io = FbxIOSettings.Create(fm, "fbx_root");
                fm.SetIOSettings(io);
            }

            FbxScene scene = FbxScene.Create(fm, "Scene");

            FbxDocumentInfo scene_info = FbxDocumentInfo.Create(fm, "SceneInfo");
            scene.SetSceneInfo(scene_info);

            // Add the scene contents
            AddModelContents(scene, data);

            // Find the ID of the FBX-ASCII filetype
            int file_format = -1;
            FbxIOPluginRegistry io_pr = fm.GetIOPluginRegistry();
            for (int i = 0; i < io_pr.GetWriterFormatCount(); i++)
            {
                if (!io_pr.WriterIsFBX(i))
                    continue;
                string desc = io_pr.GetWriterFormatDescription(i);
                // if (!desc.Contains("ascii")) continue;
                if (!desc.Contains("binary")) continue;
                // Console.WriteLine(desc);
                file_format = i;
                break;
            }

            // Save the scene
            FbxNet.FbxExporter ex = FbxNet.FbxExporter.Create(fm, "Exporter");
            ex.Initialize(path, file_format, fm.GetIOSettings());
            ex.Export(scene);

            return path;
        }

        private static void AddModelContents(FbxScene scene, FullModelData data)
        {
            // Find all the Object3Ds that are actually part of an object
            HashSet<Object3D> model_objects = new HashSet<Object3D>();
            foreach (object obj in data.parsed_sections.Values)
            {
                if (!(obj is Model m))
                    continue;

                model_objects.Add(m.object3D);
            }

            foreach (SectionHeader section_header in data.sections)
            {
                if (section_header.type != Tags.model_data_tag)
                    continue;

                Model model = (Model) data.parsed_sections[section_header.id];
                if (model.version == 6)
                    continue;

                ModelInfo mesh = AddModel(data, model);

                if (model.skinbones_ID == 0)
                {
                    // If there's no corresponding skeleton, remove the 'Object' suffix
                    mesh.Node.SetName(model.object3D.Name);

                    scene.GetRootNode().AddChild(mesh.Node);
                    continue;
                }

                SkinBones sb = (SkinBones) data.parsed_sections[model.skinbones_ID];

                Dictionary<Object3D, BoneInfo> bones = AddSkeleton(data, sb, model_objects);

                // Make one root node to contain both the skeleton and the model
                FbxNode root = FbxNode.Create(fm, model.object3D.Name);
                root.AddChild(mesh.Node);
                root.AddChild(bones[(Object3D) data.parsed_sections[sb.probably_root_bone]].Node);
                scene.GetRootNode().AddChild(root);

                // Add a root skeleton node. THis must be in the model's parent, otherwise Blender won't
                // set up the armatures correctly (or at all, actually).
                FbxSkeleton skeleton = FbxSkeleton.Create(fm, "");
                skeleton.SetSkeletonType(FbxSkeleton.EType.eRoot);
                root.SetNodeAttribute(skeleton);

                // Add the skin weights, which bind the model onto the bones
                AddWeights(data, model, sb, mesh.Mesh, bones);
            }
        }

        private static ModelInfo AddModel(FullModelData data, Model model)
        {
            Dictionary<uint, object> parsed = data.parsed_sections;
            PassthroughGP pgp = (PassthroughGP) parsed[model.passthroughGP_ID];
            Geometry geom = (Geometry) parsed[pgp.geometry_section];
            Topology topo = (Topology) parsed[pgp.topology_section];

            string name = model.object3D.Name;

            FbxNode mesh_node = FbxNode.Create(fm, name + "Object");
            FbxMesh mesh = FbxMesh.Create(fm, name + "Mesh");
            mesh_node.SetNodeAttributeGeom(mesh);

            CopyTransform(model.object3D.world_transform, mesh_node);

            FbxLayerElementNormal normals = mesh.CreateElementNormal();
            normals.SetReferenceMode(FbxLayerElement.EReferenceMode.eDirect);
            normals.SetMappingMode(FbxLayerElement.EMappingMode.eByControlPoint);

            mesh.InitControlPoints(geom.verts.Count);
            FbxVector4 temp = new FbxVector4();
            for (int i = 0; i < geom.verts.Count; i++)
            {
                temp.Set(geom.verts[i]);
                mesh.SetControlPointAt(temp, i);

                if (geom.normals.Count <= 0) continue;
                temp.Set(geom.normals[i]);
                mesh.SetControlPointNormalAt(temp, i);
            }

            foreach (Face face in topo.facelist)
            {
                mesh.BeginPolygon();
                mesh.AddPolygon(face.a);
                mesh.AddPolygon(face.b);
                mesh.AddPolygon(face.c);
                mesh.EndPolygon();
            }

            // Export the UVs
            AddUVs(mesh, "PrimaryUV", geom.uvs);
            AddUVs(mesh, "PatternUV", geom.pattern_uvs);

            if (geom.vertex_colors.Count > 0)
            {
                FbxLayerElementVertexColor colours = mesh.CreateElementVertexColor();
                colours.SetMappingMode(FbxLayerElement.EMappingMode.eByControlPoint);
                colours.SetReferenceMode(FbxLayerElement.EReferenceMode.eDirect);
                for (int i = 0; i < geom.vert_count; i++)
                {
                    GeometryColor c = geom.vertex_colors[i];
                    colours.mDirectArray.Add(new FbxColor(
                        c.red / 255.0,
                        c.green / 255.0,
                        c.blue / 255.0,
                        c.alpha / 255.0
                    ));
                }
            }

            return new ModelInfo
            {
                Model = model,
                Mesh = mesh,
                Node = mesh_node,
            };
        }

        private static void AddUVs(FbxMesh mesh, string name, List<Vector2D> uvs)
        {
            if (uvs.Count == 0)
                return;

            FbxLayerElementUV uv = mesh.CreateElementUV("PrimaryUV");
            uv.SetMappingMode(FbxLayerElement.EMappingMode.eByControlPoint);
            uv.SetReferenceMode(FbxLayerElement.EReferenceMode.eDirect);
            for (int i = 0; i < uvs.Count; i++)
            {
                uv.GetDirectArray().Add(uvs[i].ToFbxV2());
            }
        }

        private static Dictionary<Object3D, BoneInfo> AddSkeleton(FullModelData data, SkinBones bones,
            HashSet<Object3D> exclude)
        {
            Dictionary<uint, object> parsed = data.parsed_sections;
            Dictionary<Object3D, BoneInfo> bone_maps = new Dictionary<Object3D, BoneInfo>();
            Object3D root = (Object3D) parsed[bones.probably_root_bone];
            AddBone(root, bone_maps, exclude, bones);
            return bone_maps;
        }

        private static BoneInfo AddBone(Object3D obj, Dictionary<Object3D, BoneInfo> bones, HashSet<Object3D> exclude,
            SkinBones sb)
        {
            string name = obj.Name;

            // If it's not part of the SkinBones object list, then it's a locator that vertices can't bind to
            // This will be read later when importing
            if (!sb.objects.Contains(obj.id))
            {
                name += FbxUtils.LocatorSuffix;
            }

            FbxNode node = FbxNode.Create(fm, name);

            CopyTransform(obj.rotation, node);

            FbxSkeleton skel = FbxSkeleton.Create(fm, obj.Name + "Skel");
            skel.Size.Set(1);
            skel.SetSkeletonType(FbxSkeleton.EType.eLimbNode);
            node.SetNodeAttribute(skel);

            foreach (Object3D child in obj.children)
            {
                if (exclude.Contains(child))
                    continue;

                BoneInfo n = AddBone(child, bones, exclude, sb);
                node.AddChild(n.Node);
            }

            BoneInfo info = new BoneInfo
            {
                Game = obj,
                Node = node,
                Skeleton = skel,
            };

            bones[obj] = info;

            return info;
        }

        private static void AddWeights(FullModelData data, Model model, SkinBones sb,
            FbxMesh mesh, IReadOnlyDictionary<Object3D, BoneInfo> bones)
        {
            Dictionary<uint, object> parsed = data.parsed_sections;
            PassthroughGP pgp = (PassthroughGP) parsed[model.passthroughGP_ID];
            Geometry geom = (Geometry) parsed[pgp.geometry_section];

            // Mainly for testing stuff with bone exports, keep things working if
            // the model has a skeleton but no weights.
            if (geom.weights.Count == 0)
                return;

            FbxSkin skin = FbxSkin.Create(fm, model.object3D.Name + "Skin");
            mesh.AddDeformer(skin);

            for (int bone_idx = 0; bone_idx < sb.count; bone_idx++)
            {
                Object3D obj = (Object3D) parsed[sb.objects[bone_idx]];

                FbxCluster cluster = FbxCluster.Create(fm, "");
                cluster.SetLink(bones[obj].Node);
                cluster.SetLinkMode(FbxCluster.ELinkMode.eNormalize);

                // This is all AFAIK, but here's what I'm pretty sure this is doing
                // SetTransformMatrix registers the transform of the mesh
                // While SetTransformLinkMatrix binds it to the transform of the bone
                FbxAMatrix ident = new FbxAMatrix();
                ident.SetIdentity();
                cluster.SetTransformMatrix(ident);

                // Break down the bone's transform and convert it to an FBX affine matrix
                // Skip the scale for now though, we don't need it
                obj.world_transform.Decompose(out Vector3D _, out Quaternion rotate, out Vector3D translate);
                FbxAMatrix mat = new FbxAMatrix();
                mat.SetIdentity();
                mat.SetT(new FbxVector4(translate.X, translate.Y, translate.Z));
                mat.SetQ(new FbxQuaternion(rotate.X, rotate.Y, rotate.Z, rotate.W));

                // And lode that in as the bone (what it's linked to) transform matrix
                cluster.SetTransformLinkMatrix(mat);

                for (int i = 0; i < geom.verts.Count; i++)
                {
                    GeometryWeightGroups groups = geom.weight_groups[i];
                    Vector3D weights = geom.weights[i];
                    float weight;

                    if (bone_idx == 0)
                        continue;
                    int bi = bone_idx;

                    if (groups.Bones1 == bi)
                        weight = weights.X;
                    else if (groups.Bones2 == bi)
                        weight = weights.Y;
                    else if (groups.Bones3 == bi)
                        weight = weights.Z;
                    else if (groups.Bones4 == bi)
                        throw new Exception("Unsupported Bone4 weight - not in weights");
                    else
                        continue;

                    cluster.AddControlPointIndex(i, weight);
                }

                if (!skin.AddCluster(cluster))
                    throw new Exception();
            }
        }

        private static void CopyTransform(Matrix3D transform, FbxNode node)
        {
            Vector3D translate;
            Quaternion rotate;
            Vector3D scale;
            transform.Decompose(out scale, out rotate, out translate);

            node.LclTranslation.Set(new FbxDouble3(translate.X, translate.Y, translate.Z));

            // FbxQuaternion fq = new FbxQuaternion(rotate.X, rotate.Y, rotate.Z, rotate.W);
            // node.LclRotation.Set(fq.DecomposeSphericalXYZ().D3());

            node.RotationOrder.Set(FbxEuler.EOrder.eOrderZYX);
            Vector3D euler = rotate.ToEulerZYX() * (180 / (float) Math.PI);
            node.LclRotation.Set(euler.ToFbxD3());
        }

        private struct ModelInfo
        {
            public Model Model;
            public FbxMesh Mesh;
            public FbxNode Node;
        }

        private struct BoneInfo
        {
            public Object3D Game;
            public FbxNode Node;
            public FbxSkeleton Skeleton;
        }
    }
}
