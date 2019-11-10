using System;
using System.Collections.Generic;
using System.Linq;
using FbxNet;
using Nexus;
using PD2ModelParser.Misc;
using PD2ModelParser.Sections;

namespace PD2ModelParser.Importers
{
    public class FilmboxImporter
    {
        private static FbxManager _fm;

        public static void Import(FullModelData data, string filepath, bool addNew,
            Func<object, Object3D> rootPointResolver)
        {
            if (_fm == null)
                _fm = FbxManager.Create();

            FbxImporter importer = FbxImporter.Create(_fm, "");
            bool result = importer.Initialize(filepath);
            if (!result)
                throw new Exception("Cannot load FBX file");

            FbxScene scene = FbxScene.Create(_fm, "");
            result = importer.Import(scene);
            // TODO add FbxIOBase to FbxNet so we can access the error code
            if (!result)
                throw new Exception("Cannot import FBX file");

            FilmboxImporter imp = new FilmboxImporter(data);

            List<FbxNode> meshes = new List<FbxNode>();
            imp.RecurseMeshes(scene.GetRootNode(), meshes);

            foreach (FbxNode node in meshes)
            {
                FbxMesh mesh = node.GetMesh();

                Object3D parent = rootPointResolver.Invoke(node);

                imp.AddMesh(parent, node, mesh);
            }
        }

        private readonly FullModelData data;
        private readonly Dictionary<Object3D, Model> _modelObjects = new Dictionary<Object3D, Model>();
        private readonly Dictionary<ulong, Object3D> _objects = new Dictionary<ulong, Object3D>();

        private FilmboxImporter(FullModelData data)
        {
            this.data = data;

            foreach (object item in data.parsed_sections.Values)
            {
                if (item is Model m)
                {
                    _modelObjects[m.object3D] = m;

                    // While it's not a 'real' Object3D in that it's embedded into Model, make it
                    // available for access later.
                    _objects[m.object3D.hashname.Hash] = m.object3D;
                }

                if (!(item is Object3D obj))
                    continue;

                _objects[obj.hashname.Hash] = obj;
            }
        }

        private Model CreateEmptyMesh(Object3D parent, string name)
        {
            // The basic geometry information - vertices, normals, UVs, but notably no faces
            Geometry geom = CreateGeometry();

            // Faces
            Topology topo = new Topology(0, name);
            data.AddSection(topo);

            // Weird wrappers
            TopologyIP tip = new TopologyIP(0, topo);
            data.AddSection(tip);
            PassthroughGP pgp = new PassthroughGP(0, geom, topo);
            data.AddSection(pgp);

            // Material information
            // TODO material setup
            Material mat = new Material(0, "");
            data.AddSection(mat);
            Material_Group mat_g = new Material_Group(0, mat.id);
            data.AddSection(mat_g);

            // Used for some internal model stuff
            obj_data fake_obj = new obj_data
            {
                object_name = name,
                verts = geom.verts,
                faces = topo.facelist,
            };

            // Build the model itself
            Model model = new Model(fake_obj, pgp, tip, mat_g, parent);
            data.AddSection(model);

            return model;
        }

        private Model AddMesh(Object3D parent, FbxNode node, FbxMesh mesh)
        {
            FbxNode root = node;
            if (node.GetParent()?.GetSkeleton() != null)
            {
                root = node.GetParent();
            }

            Model model;
            if (_objects.TryGetValue(Hash64.HashString(root.GetName()), out Object3D existing_object))
            {
                model = _modelObjects[existing_object];
            }
            else
            {
                model = CreateEmptyMesh(parent, root.GetName());
            }

            Dictionary<uint, object> parsed = data.parsed_sections;
            PassthroughGP pgp = (PassthroughGP) parsed[model.passthroughGP_ID];
            Geometry geom = (Geometry) parsed[pgp.geometry_section];
            Topology topo = (Topology) parsed[pgp.topology_section];

            BuildGeometry(mesh, geom);
            BuildTopology(topo, mesh);

            BuildUVs(mesh, geom);

            // Add the bones - note this *only* adds the skeleton, and not any weights
            Dictionary<ulong, Object3D> skel = AddSkeleton(root, model, parent, out Object3D root_bone);
            if (skel == null)
                return model;

            // Parent the model to the root bone, as per the cop model where the meshes are parented to
            // the hip bone.
            // Note that the model only had one skeleton, shared between all models - this will probably break
            // it quite a bit if we try and export them all back in.
            model.object3D.parent = root_bone;

            AddWeights(mesh, skel, model, geom);

            return model;
        }

        private Dictionary<ulong, Object3D> AddSkeleton(FbxNode rootNode, Model model,
            Object3D rootPoint, out Object3D rootBone)
        {
            FbxSkeleton root_skeleton = rootNode.GetSkeleton();
            if (root_skeleton == null)
            {
                rootBone = null;
                return null;
            }

            Dictionary<ulong, Object3D> objs = new Dictionary<ulong, Object3D>();

            Object3D root_bone = null;

            SkinBones sb = new SkinBones(0);
            data.AddSection(sb);
            model.skinbones_ID = sb.id;

            Matrix3D offset_transform = Matrix3D.Identity;

            Recurse(rootNode, rootPoint, (node, parent) =>
            {
                FbxSkeleton skel = node.GetSkeleton();
                if (skel == null || skel.GetSkeletonType() == FbxSkeleton.EType.eRoot)
                    return parent;

                // Look up if there's an existing object matching this object
                _objects.TryGetValue(Hash64.HashString(node.GetName()), out Object3D obj);

                if (obj == null)
                {
                    obj = new Object3D(node.GetName(), parent);
                    parent?.children?.Add(obj);
                    data.AddSection(obj);
                }

                if (root_bone == null)
                {
                    root_bone = obj;
                    sb.global_skin_transform = obj.rotation;

                    offset_transform = sb.global_skin_transform;
                    offset_transform.Invert();
                }
                else if (parent == rootPoint)
                {
                    throw new Exception("Each rigged model must have only one root bone");
                }

                objs[node.PtrHashCode()] = obj;

                // Note the field is named badly - it's a transform, not just a rotation
                obj.rotation = node.GetNexusTransform();

                sb.objects.Add(obj.id);

                // TODO implement
                // ZNix's 10/11/19 notes on how the rotation and global_skin_transform seem
                // to work (on ene_security_3):
                // global_skin transform holds the root bone's transform, and seems to be applied
                // to everything else in sb.rotations. Objects then have entries in sb.rotations
                // that move them back to their position relative to the root node, undoing
                // global_skin_transform (to get their in-model position) and undoing it again
                // to get their position relative to the root bone.
                // This seems to correctly position the hips (and stepping through with the debugger
                // confirms that the sb.rotations matrix produced matches that in the original source
                // model, however all other bones are broken.
                sb.rotations.Add(offset_transform * offset_transform * obj.rotation);

                // TODO sb.bones.bones

                return obj;
            });

            // No bones :(
            // We could continue here, but it's almost certainly not what the
            // user would expect and a loud error is almost always better than
            // a silent failure.
            if (root_bone == null)
                throw new Exception("Rigged model " + rootNode.GetName() + " has no bones");

            sb.probably_root_bone = root_bone.id;
            rootBone = root_bone;

            // TODO more research into how this works
            // This seems to be an index mapping through to bones from RenderAtoms
            BoneMappingItem bmi = new BoneMappingItem();
            for (uint i = 0; i < sb.count; i++)
                bmi.bones.Add(i);

            for (int i = 0; i < model.renderAtoms.Count; i++)
                sb.bones.bone_mappings.Add(bmi);

            // TODO setup the other SkinBones fields - probably very important for Diesel
            return objs;
        }

        private void AddWeights(FbxMesh mesh,
            Dictionary<ulong, Object3D> skel, Model model, Geometry geom)
        {
            int deformer_count = mesh.GetDeformerCount(FbxDeformer.EDeformerType.eSkin);
            if (deformer_count == 0) return;
            if (deformer_count != 1)
                throw new Exception("Only one skin per mesh is supported");

            FbxSkin skin = mesh.GetDeformer(0, FbxDeformer.EDeformerType.eSkin).CastToSkin();
            if (skin == null)
                throw new Exception("Could not get skin deformer ID=0");

            SkinBones sb = (SkinBones) data.parsed_sections[model.skinbones_ID];

            // Either 2 for low-LOD models or 3 for high-LOD models - afaik this
            // has something to do with which render template is used.
            // TODO confirm if this is true, and if so allow selection of the render template somehow
            geom.headers.Add(new GeometryHeader(3, GeometryChannelTypes.BLENDWEIGHT));

            // Odd size, but consistent when taken from default models
            geom.headers.Add(new GeometryHeader(7, GeometryChannelTypes.BLENDINDICES));

            // Build a lookup table to find the index of a given bone
            Dictionary<Object3D, int> bone_indices = new Dictionary<Object3D, int>();
            for (int i = 0; i < sb.count; i++)
            {
                Object3D obj = (Object3D) data.parsed_sections[sb.objects[i]];
                bone_indices[obj] = i;
            }

            // FBX (roughly, via clusters) stores the vertices/weights for each bone, while Diesel
            // stores the bones/weights for each vertex. This list corresponds to each
            // vertex in the model so we can flip this around.
            List<WeightPart>[] parts = new List<WeightPart>[geom.vert_count];
            for (int i = 0; i < geom.vert_count; i++)
                parts[i] = new List<WeightPart>();

            for (int i = 0; i < skin.GetClusterCount(); i++)
            {
                FbxCluster cluster = skin.GetCluster(i);

                FbxNode bone_node = cluster.GetLink();
                Object3D bone = skel[bone_node.PtrHashCode()];
                int idx = bone_indices[bone];

                SWIGTYPE_p_int indices = cluster.GetControlPointIndices();
                SWIGTYPE_p_double weights = cluster.GetControlPointWeights();
                for (int j = 0; j < cluster.GetControlPointIndicesCount(); j++)
                {
                    int vert_idx = FbxNet.FbxNet.intArray_getitem(indices, j);
                    double weight = FbxNet.FbxNet.doubleArray_getitem(weights, j);

                    List<WeightPart> vert = parts[vert_idx];

                    if (vert.Any(p => p.boneID == idx))
                        throw new Exception("Two clusters for the same bone and vertex " +
                                            "are currently unsupported");

                    vert.Add(new WeightPart
                    {
                        boneID = idx,
                        weight = (float) weight,
                    });
                }
            }

            for (int i = 0; i < geom.vert_count; i++)
            {
                AddWeightsForVertex(parts[i], geom);
            }
        }

        private void AddWeightsForVertex(List<WeightPart> parts, Geometry geom)
        {
            // AFAIK this is affected by the header thing - see above
            // TODO should we quietly just chop off the least important few weights?
            if (parts.Count > 3)
                throw new Exception("Vertices cannot be affected by more than three bones");

            Vector3D weights = Vector3D.Zero;
            GeometryWeightGroups groups = new GeometryWeightGroups();

            int wi = 0;
            foreach (WeightPart part in parts.OrderByDescending(v => v.weight))
            {
                if (part.boneID > ushort.MaxValue)
                    throw new Exception("Too many bones!");

                weights[wi] = part.weight;

                ushort bid = (ushort) part.boneID;
                switch (wi)
                {
                    case 0:
                        groups.Bones1 = bid;
                        break;
                    case 1:
                        groups.Bones2 = bid;
                        break;
                    case 2:
                        groups.Bones3 = bid;
                        break;
                    default:
                        throw new Exception(); // Should already be stopped above
                }

                wi++;
            }

            geom.weights.Add(weights);
            geom.weight_groups.Add(groups);
        }

        private Geometry CreateGeometry()
        {
            Geometry geom = new Geometry(0);
            data.AddSection(geom);

            // TODO cleanup
            geom.headers.Add(new GeometryHeader(3, GeometryChannelTypes.POSITION)); // vert
            geom.headers.Add(new GeometryHeader(2, GeometryChannelTypes.TEXCOORD0)); // uv
            geom.headers.Add(new GeometryHeader(3, GeometryChannelTypes.NORMAL0)); // norm
            geom.headers.Add(new GeometryHeader(3, GeometryChannelTypes.BINORMAL0)); // unk20
            geom.headers.Add(new GeometryHeader(3, GeometryChannelTypes.TANGENT0)); // unk21

            return geom;
        }

        private void BuildGeometry(FbxMesh mesh, Geometry geom)
        {
            geom.vert_count = (uint) mesh.GetControlPointsCount();

            geom.verts.Clear();
            geom.normals.Clear();
            geom.vertex_colors.Clear();
            foreach (List<Vector2D> uvs in geom.UVs) uvs.Clear();

            FbxLayerElementNormal normals = mesh.GetElementNormal();
            if (normals.GetMappingMode() != FbxLayerElement.EMappingMode.eByControlPoint)
                throw new Exception("Normals must be mapped by control point");

            if (normals.GetReferenceMode() != FbxLayerElement.EReferenceMode.eDirect)
                throw new Exception("Normals must be referenced direct");

            for (int i = 0; i < mesh.GetControlPointsCount(); i++)
            {
                FbxVector4 v = mesh.GetControlPointAt(i);
                geom.verts.Add(v.V3());

                FbxVector4 n = normals.GetDirectArray().GetAt(i);
                geom.normals.Add(n.V3());

                if (n.V3().Length() < 0.1)
                    throw new Exception("Short normal!");

                // Normally I don't care about leaving stuff around as it'll be cleaned
                // up when the C# GC eats it, but in this case it might be a bit too much.
                v.Dispose();
                n.Dispose();
            }

            AddVertexColours(mesh, geom);
        }

        private void AddVertexColours(FbxMesh mesh, Geometry geom)
        {
            int vertex_colour_count = mesh.GetElementVertexColorCount();
            if (vertex_colour_count > 1)
                throw new Exception("The model tool does not support more than one vertex colour layer");

            if (vertex_colour_count == 0) return;

            // TODO confirm the size is indeed 3
            geom.headers.Add(new GeometryHeader(3, GeometryChannelTypes.COLOR));
            FbxLayerElementVertexColor layer = mesh.GetElementVertexColor();

            if (layer.GetMappingMode() != FbxLayerElement.EMappingMode.eByControlPoint)
                throw new Exception("Vertex colour: only per-vertex colouring is supported");

            if (layer.GetReferenceMode() != FbxLayerElement.EReferenceMode.eDirect)
                throw new Exception("Vertex colour: only per-vertex colouring is supported");

            FbxLayerElementArrayTemplateColour array = layer.GetDirectArray();

            if (array.GetCount() != geom.vert_count)
                throw new Exception("Vertex colour: mismatched vertex count");

            for (int i = 0; i < array.GetCount(); i++)
            {
                FbxColor colour = array.GetAt(i);
                geom.vertex_colors.Add(colour.ToGeomColour());
            }
        }

        private void BuildTopology(Topology topo, FbxMesh mesh)
        {
            topo.facelist.Clear();

            for (int i = 0; i < mesh.GetPolygonCount(); i++)
            {
                int size = mesh.GetPolygonSize(i);
                if (size != 3)
                    throw new Exception("Triangles are the only supported type of polygon");

                int a = mesh.GetPolygonVertex(i, 0);
                int b = mesh.GetPolygonVertex(i, 1);
                int c = mesh.GetPolygonVertex(i, 2);
                // TODO verify the indices are within bounds
                Face f = new Face {a = (ushort) a, b = (ushort) b, c = (ushort) c};
                topo.facelist.Add(f);
            }
        }

        private void BuildUVs(FbxMesh mesh, Geometry geom)
        {
            for (int i = 0; i < mesh.GetElementUVCount(); i++)
            {
                FbxLayerElementUV layer = mesh.GetElementUV(i);

                int gi;
                switch (layer.GetName())
                {
                    case "PrimaryUV":
                        gi = 0;
                        break;
                    default:
                        throw new Exception("Unknown UV " + layer.GetName());
                }

                if (layer.GetMappingMode() != FbxLayerElement.EMappingMode.eByControlPoint)
                    throw new Exception("UV: only per-vertex UVs are supported");

                if (layer.GetReferenceMode() != FbxLayerElement.EReferenceMode.eDirect)
                    throw new Exception("UV: only direct indexing is supported");

                FbxLayerElementArrayTemplateVector2 direct = layer.GetDirectArray();
                List<Vector2D> uv = geom.UVs[gi];

                for (int j = 0; j < direct.GetCount(); j++)
                {
                    uv.Add(direct.GetAt(j).V2());
                }
            }
        }

        private void RecurseMeshes(FbxNode root, List<FbxNode> meshes)
        {
            Recurse<object>(root, null, (node, ud) =>
            {
                FbxMesh mesh = node.GetMesh();
                if (mesh == null)
                    return null;

                meshes.Add(node);
                return null;
            });
        }

        private void Recurse<T>(FbxNode node, T ud, Func<FbxNode, T, T> callback)
        {
            T sub = callback(node, ud);
            for (int i = 0; i < node.GetChildCount(); i++)
            {
                FbxNode child = node.GetChild(i);
                Recurse(child, sub, callback);
            }
        }

        private class WeightPart
        {
            public int boneID;
            public float weight;
        }
    }
}
