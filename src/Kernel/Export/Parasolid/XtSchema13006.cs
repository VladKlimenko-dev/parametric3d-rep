namespace Parametric3D.Kernel.Export.Parasolid;

/// <summary>
/// БАЗОВАЯ СХЕМА PARASOLID XT SCH_13006 (Parasolid V13) — раскладка узлов: на каждый тип — имя, признак узла переменной длины
/// и ПЕРЕДАВАЕМЫЕ поля по порядку (имя, код типа, длина массива: 0 — одно значение, 1 — поле переменной длины, n &gt; 1 —
/// массив; класс указателя). Файлы новых версий (SolidWorks 2015+, Parasolid 30–35) записывают отличия своей схемы от этой
/// (Parasolid XT Format Reference, «Embedded schemas»), поэтому для чтения любой версии достаточно базовой. Таблица выписана
/// из файла схемы sch_13006 (распространяется с ps-parser, MIT, github.com/khoanguyen-3fc/ps-parser) — только передаваемые
/// поля, нужные для разбора потока.
/// Коды: u — байт, c — знак, l — логическое, n — короткое целое, w — знак Unicode, d — целое, p — указатель, f — число,
/// i — интервал (2 числа), v — вектор (3 числа), h — однородный вектор (3 числа в потоке), b — коробка (6 чисел).
/// </summary>
internal static class XtSchema13006
{
    internal readonly record struct Field(string Name, char Code, int N, int PtrClass);
    internal sealed record NodeType(int Id, string Name, bool Variable, Field[] Fields);

    internal static readonly Dictionary<int, NodeType> Types = Build();

    static Dictionary<int, NodeType> Build()
    {
        var d = new Dictionary<int, NodeType>();
        void T(int id, string name, bool variable, params Field[] f) => d[id] = new NodeType(id, name, variable, f);
        static Field F(string n, char c, int k = 0, int pc = 0) => new(n, c, k, pc);
        T(1, "NULLP", false);
        T(2, "WORKSPACE", true, F("ws", 'c', 1));
        T(3, "PARTITION", false, F("current_pmark", 'p', 0, 4), F("highest_id", 'd'));
        T(4, "PMARK", false, F("preceding", 'p', 0, 4), F("first_following", 'p', 0, 4), F("next_sibling", 'p', 0, 4), F("prev_sibling", 'p', 0, 4), F("n_new_nodes", 'd'), F("n_del_nodes", 'd'), F("n_copy_mod_nodes", 'd'), F("delta_key", 'd'), F("delta_is_forward", 'l'), F("id", 'd'));
        T(5, "UNIVERSE", false, F("first_partition", 'p', 0, 3), F("current_mark", 'p', 0, 9), F("pmarks_at_current", 'p', 0, 170), F("root_tag_table", 'p', 0, 8), F("session_data", 'p', 0, 172), F("new", 'p', 0, 168), F("del", 'p', 0, 168));
        T(6, "LEAF_TAG_TABLE", false, F("entity", 'p', 256, 1001), F("n_live", 'd'));
        T(7, "BRANCH_TAG_TABLE", false, F("leaf", 'p', 1024, 6), F("n_live", 'd'));
        T(8, "ROOT_TAG_TABLE", false, F("tag_limit", 'd'), F("null_branch", 'p', 0, 7), F("null_leaf", 'p', 0, 6), F("next_low_tag", 'd'), F("next_high_tag", 'd'), F("branch", 'p', 4096, 7));
        T(9, "MARK", false, F("following", 'p', 0, 9), F("preceding", 'p', 0, 9), F("pmarks_from_preceding", 'p', 0, 170), F("pmarks_from_following", 'p', 0, 170));
        T(10, "ASSEMBLY", false, F("highest_node_id", 'd'), F("attributes_features", 'p', 0, 1019), F("attribute_chains", 'p', 0, 70), F("list", 'p', 0, 70), F("surface", 'p', 0, 1006), F("curve", 'p', 0, 1008), F("point", 'p', 0, 29), F("key", 'p', 0, 102), F("res_size", 'f'), F("res_linear", 'f'), F("ref_instance", 'p', 0, 11), F("next", 'p', 0, 10), F("previous", 'p', 0, 10), F("state", 'u'), F("owner", 'p', 0, 101), F("type", 'u'), F("sub_instance", 'p', 0, 11));
        T(11, "INSTANCE", false, F("node_id", 'd'), F("attributes_features", 'p', 0, 1019), F("type", 'u'), F("part", 'p', 0, 1005), F("transform", 'p', 0, 100), F("assembly", 'p', 0, 10), F("next_in_part", 'p', 0, 11), F("prev_in_part", 'p', 0, 11), F("next_of_part", 'p', 0, 11), F("prev_of_part", 'p', 0, 11));
        T(12, "BODY", false, F("highest_node_id", 'd'), F("attributes_features", 'p', 0, 1019), F("attribute_chains", 'p', 0, 70), F("surface", 'p', 0, 1006), F("curve", 'p', 0, 1008), F("point", 'p', 0, 29), F("key", 'p', 0, 102), F("res_size", 'f'), F("res_linear", 'f'), F("ref_instance", 'p', 0, 11), F("next", 'p', 0, 12), F("previous", 'p', 0, 12), F("state", 'u'), F("owner", 'p', 0, 101), F("body_type", 'u'), F("nom_geom_state", 'u'), F("shell", 'p', 0, 13), F("boundary_surface", 'p', 0, 1006), F("boundary_curve", 'p', 0, 1008), F("boundary_point", 'p', 0, 29), F("region", 'p', 0, 19), F("edge", 'p', 0, 16), F("vertex", 'p', 0, 18));
        T(13, "SHELL", false, F("node_id", 'd'), F("attributes_features", 'p', 0, 1019), F("body", 'p', 0, 12), F("next", 'p', 0, 13), F("face", 'p', 0, 14), F("edge", 'p', 0, 16), F("vertex", 'p', 0, 18), F("region", 'p', 0, 19), F("front_face", 'p', 0, 14));
        T(14, "FACE", false, F("node_id", 'd'), F("attributes_features", 'p', 0, 1019), F("tolerance", 'f'), F("next", 'p', 0, 14), F("previous", 'p', 0, 14), F("loop", 'p', 0, 15), F("shell", 'p', 0, 13), F("surface", 'p', 0, 1006), F("sense", 'c'), F("next_on_surface", 'p', 0, 14), F("previous_on_surface", 'p', 0, 14), F("next_front", 'p', 0, 14), F("previous_front", 'p', 0, 14), F("front_shell", 'p', 0, 13));
        T(15, "LOOP", false, F("node_id", 'd'), F("attributes_features", 'p', 0, 1019), F("halfedge", 'p', 0, 17), F("face", 'p', 0, 14), F("next", 'p', 0, 15));
        T(16, "EDGE", false, F("node_id", 'd'), F("attributes_features", 'p', 0, 1019), F("tolerance", 'f'), F("halfedge", 'p', 0, 17), F("previous", 'p', 0, 16), F("next", 'p', 0, 16), F("curve", 'p', 0, 1008), F("next_on_curve", 'p', 0, 16), F("previous_on_curve", 'p', 0, 16), F("owner", 'p', 0, 1029));
        T(17, "HALFEDGE", false, F("attributes_features", 'p', 0, 1019), F("loop", 'p', 0, 15), F("forward", 'p', 0, 17), F("backward", 'p', 0, 17), F("vertex", 'p', 0, 18), F("other", 'p', 0, 17), F("edge", 'p', 0, 16), F("curve", 'p', 0, 1008), F("next_at_vx", 'p', 0, 17), F("sense", 'c'));
        T(18, "VERTEX", false, F("node_id", 'd'), F("attributes_features", 'p', 0, 1019), F("halfedge", 'p', 0, 17), F("previous", 'p', 0, 18), F("next", 'p', 0, 18), F("point", 'p', 0, 29), F("tolerance", 'f'), F("owner", 'p', 0, 1029));
        T(19, "REGION", false, F("node_id", 'd'), F("attributes_features", 'p', 0, 1019), F("body", 'p', 0, 12), F("next", 'p', 0, 19), F("previous", 'p', 0, 19), F("shell", 'p', 0, 13), F("type", 'c'));
        T(29, "POINT", false, F("node_id", 'd'), F("attributes_features", 'p', 0, 1019), F("owner", 'p', 0, 1011), F("next", 'p', 0, 29), F("previous", 'p', 0, 29), F("pvec", 'v'));
        T(30, "LINE", false, F("node_id", 'd'), F("attributes_features", 'p', 0, 1019), F("owner", 'p', 0, 1010), F("next", 'p', 0, 1008), F("previous", 'p', 0, 1008), F("geometric_owner", 'p', 0, 141), F("sense", 'c'), F("pvec", 'v'), F("direction", 'v'));
        T(31, "CIRCLE", false, F("node_id", 'd'), F("attributes_features", 'p', 0, 1019), F("owner", 'p', 0, 1010), F("next", 'p', 0, 1008), F("previous", 'p', 0, 1008), F("geometric_owner", 'p', 0, 141), F("sense", 'c'), F("centre", 'v'), F("normal", 'v'), F("x_axis", 'v'), F("radius", 'f'));
        T(32, "ELLIPSE", false, F("node_id", 'd'), F("attributes_features", 'p', 0, 1019), F("owner", 'p', 0, 1010), F("next", 'p', 0, 1008), F("previous", 'p', 0, 1008), F("geometric_owner", 'p', 0, 141), F("sense", 'c'), F("centre", 'v'), F("normal", 'v'), F("x_axis", 'v'), F("major_radius", 'f'), F("minor_radius", 'f'));
        T(33, "PARABOLA", false, F("node_id", 'd'), F("attributes_features", 'p', 0, 1019), F("owner", 'p', 0, 1010), F("next", 'p', 0, 1008), F("previous", 'p', 0, 1008), F("geometric_owner", 'p', 0, 141), F("sense", 'c'), F("origin", 'v'), F("normal", 'v'), F("x_axis", 'v'), F("focal_length", 'f'));
        T(34, "HYPERBOLA", false, F("node_id", 'd'), F("attributes_features", 'p', 0, 1019), F("owner", 'p', 0, 1010), F("next", 'p', 0, 1008), F("previous", 'p', 0, 1008), F("geometric_owner", 'p', 0, 141), F("sense", 'c'), F("origin", 'v'), F("normal", 'v'), F("x_axis", 'v'), F("transverse_radius", 'f'), F("conjugate_radius", 'f'));
        T(35, "PARACURVE", false, F("node_id", 'd'), F("attributes_features", 'p', 0, 1019), F("owner", 'p', 0, 1010), F("next", 'p', 0, 1008), F("previous", 'p', 0, 1008), F("geometric_owner", 'p', 0, 141), F("sense", 'c'), F("seg", 'd'), F("cpc", 'p', 0, 48));
        T(36, "OBSOLETE_CPC", true, F("node_id", 'd'), F("attributes_features", 'p', 0, 1019), F("owner", 'p', 0, 1010), F("next", 'p', 0, 1008), F("previous", 'p', 0, 1008), F("geometric_owner", 'p', 0, 141), F("sense", 'c'), F("vertex_dimension", 'n'), F("segment_count", 'n'), F("order", 'n'), F("segment", 'p', 1, 1025));
        T(37, "PATCH_BOUND", false, F("node_id", 'd'), F("attributes_features", 'p', 0, 1019), F("owner", 'p', 0, 1010), F("next", 'p', 0, 1008), F("previous", 'p', 0, 1008), F("geometric_owner", 'p', 0, 141), F("sense", 'c'), F("boundary", 'c'), F("lh_patch", 'p', 0, 61), F("rh_patch", 'p', 0, 61));
        T(38, "INTERSECTION", false, F("node_id", 'd'), F("attributes_features", 'p', 0, 1019), F("owner", 'p', 0, 1010), F("next", 'p', 0, 1008), F("previous", 'p', 0, 1008), F("geometric_owner", 'p', 0, 141), F("sense", 'c'), F("surface", 'p', 2, 1006), F("chart", 'p', 0, 40), F("start", 'p', 0, 41), F("end", 'p', 0, 41));
        T(39, "SILHOUETTE", false, F("node_id", 'd'), F("attributes_features", 'p', 0, 1019), F("owner", 'p', 0, 1010), F("next", 'p', 0, 1008), F("previous", 'p', 0, 1008), F("geometric_owner", 'p', 0, 141), F("sense", 'c'), F("analytic_root", 'c'), F("from_infinity", 'l'), F("surface", 'p', 0, 1006), F("start", 'p', 0, 41), F("end", 'p', 0, 41), F("eye", 'v'));
        T(40, "CHART", true, F("base_parameter", 'f'), F("base_scale", 'f'), F("chart_count", 'd'), F("chordal_error", 'f'), F("angular_error", 'f'), F("parameter_error", 'f', 2), F("hvec", 'h', 1));
        T(41, "LIMIT", true, F("type", 'c'), F("hvec", 'h', 1));
        T(42, "BASIC_SEGMENT", true, F("t_length", 'f'), F("bezier_vertices", 'f', 1));
        T(43, "BSPLINE_CURVE", false, F("knot_vector", 'p', 0, 44), F("vertex_dimension", 'n'), F("vertex_count", 'd'), F("order", 'n'), F("bspline_vertices", 'p', 0, 45));
        T(44, "KNOT_VECTOR", true, F("periodic", 'l'), F("knot_count", 'd'), F("knots", 'f', 1));
        T(45, "BSPLINE_VERTICES", true, F("vertices", 'f', 1));
        T(46, "OFFSET_CURVE", false, F("node_id", 'd'), F("attributes_features", 'p', 0, 1019), F("owner", 'p', 0, 1010), F("next", 'p', 0, 1008), F("previous", 'p', 0, 1008), F("geometric_owner", 'p', 0, 141), F("sense", 'c'), F("surface", 'p', 0, 1006), F("curve", 'p', 0, 1008), F("offset", 'f'));
        T(47, "SUPER_SEGMENT", false, F("split", 'f'), F("first", 'p', 0, 1025), F("last", 'p', 0, 1025), F("t_length", 'f'));
        T(48, "CPC", false, F("node_id", 'd'), F("attributes_features", 'p', 0, 1019), F("owner", 'p', 0, 1010), F("next", 'p', 0, 1008), F("previous", 'p', 0, 1008), F("geometric_owner", 'p', 0, 141), F("sense", 'c'), F("bezier", 'p', 0, 103), F("bspline", 'p', 0, 43));
        T(49, "OBSOLETE_SP_CURVE", true, F("node_id", 'd'), F("attributes_features", 'p', 0, 1019), F("owner", 'p', 0, 1010), F("next", 'p', 0, 1008), F("previous", 'p', 0, 1008), F("geometric_owner", 'p', 0, 141), F("sense", 'c'), F("const_param", 'l'), F("segment_count", 'n'), F("order", 'n'), F("surface", 'p', 0, 1006), F("bezier_vertices", 'f', 1));
        T(50, "PLANE", false, F("node_id", 'd'), F("attributes_features", 'p', 0, 1019), F("owner", 'p', 0, 1007), F("next", 'p', 0, 1006), F("previous", 'p', 0, 1006), F("geometric_owner", 'p', 0, 141), F("sense", 'c'), F("pvec", 'v'), F("normal", 'v'), F("x_axis", 'v'));
        T(51, "CYLINDER", false, F("node_id", 'd'), F("attributes_features", 'p', 0, 1019), F("owner", 'p', 0, 1007), F("next", 'p', 0, 1006), F("previous", 'p', 0, 1006), F("geometric_owner", 'p', 0, 141), F("sense", 'c'), F("pvec", 'v'), F("axis", 'v'), F("radius", 'f'), F("x_axis", 'v'));
        T(52, "CONE", false, F("node_id", 'd'), F("attributes_features", 'p', 0, 1019), F("owner", 'p', 0, 1007), F("next", 'p', 0, 1006), F("previous", 'p', 0, 1006), F("geometric_owner", 'p', 0, 141), F("sense", 'c'), F("pvec", 'v'), F("axis", 'v'), F("radius", 'f'), F("sin_half_angle", 'f'), F("cos_half_angle", 'f'), F("x_axis", 'v'));
        T(53, "SPHERE", false, F("node_id", 'd'), F("attributes_features", 'p', 0, 1019), F("owner", 'p', 0, 1007), F("next", 'p', 0, 1006), F("previous", 'p', 0, 1006), F("geometric_owner", 'p', 0, 141), F("sense", 'c'), F("centre", 'v'), F("radius", 'f'), F("axis", 'v'), F("x_axis", 'v'));
        T(54, "TORUS", false, F("node_id", 'd'), F("attributes_features", 'p', 0, 1019), F("owner", 'p', 0, 1007), F("next", 'p', 0, 1006), F("previous", 'p', 0, 1006), F("geometric_owner", 'p', 0, 141), F("sense", 'c'), F("centre", 'v'), F("axis", 'v'), F("major_radius", 'f'), F("minor_radius", 'f'), F("x_axis", 'v'));
        T(55, "PIPE", false, F("node_id", 'd'), F("attributes_features", 'p', 0, 1019), F("owner", 'p', 0, 1007), F("next", 'p', 0, 1006), F("previous", 'p', 0, 1006), F("geometric_owner", 'p', 0, 141), F("sense", 'c'), F("spine", 'p', 0, 1008), F("radius", 'f'));
        T(56, "BLENDED_EDGE", false, F("node_id", 'd'), F("attributes_features", 'p', 0, 1019), F("owner", 'p', 0, 1007), F("next", 'p', 0, 1006), F("previous", 'p', 0, 1006), F("geometric_owner", 'p', 0, 141), F("sense", 'c'), F("blend_type", 'c'), F("surface", 'p', 2, 1006), F("spine", 'p', 0, 1008), F("range", 'f', 2), F("thumb_weight", 'f', 2), F("boundary", 'p', 2, 1006), F("start", 'p', 0, 41), F("end", 'p', 0, 41));
        T(57, "BLENDED_VERTEX", false, F("node_id", 'd'), F("attributes_features", 'p', 0, 1019), F("owner", 'p', 0, 1007), F("next", 'p', 0, 1006), F("previous", 'p', 0, 1006), F("geometric_owner", 'p', 0, 141), F("sense", 'c'), F("blend_type", 'c'), F("surface", 'p', 3, 1006), F("sub_surface", 'p', 3, 1006), F("boundary", 'p', 3, 1006), F("range", 'f', 3), F("thumb_weight", 'f', 3), F("centre", 'v'));
        T(58, "BLEND_OVERLAP", false, F("node_id", 'd'), F("attributes_features", 'p', 0, 1019), F("owner", 'p', 0, 1007), F("next", 'p', 0, 1006), F("previous", 'p', 0, 1006), F("geometric_owner", 'p', 0, 141), F("sense", 'c'), F("surface", 'p', 2, 1006), F("sub_surface", 'p', 4, 1006), F("range", 'f', 4), F("thumb_weight", 'f', 4), F("blend_type", 'c', 2), F("overlap_type", 'c'), F("swap_u_v", 'l'));
        T(59, "BLEND_BOUND", false, F("node_id", 'd'), F("attributes_features", 'p', 0, 1019), F("owner", 'p', 0, 1007), F("next", 'p', 0, 1006), F("previous", 'p', 0, 1006), F("geometric_owner", 'p', 0, 141), F("sense", 'c'), F("boundary", 'n'), F("blend", 'p', 0, 1006));
        T(60, "OFFSET_SURF", false, F("node_id", 'd'), F("attributes_features", 'p', 0, 1019), F("owner", 'p', 0, 1007), F("next", 'p', 0, 1006), F("previous", 'p', 0, 1006), F("geometric_owner", 'p', 0, 141), F("sense", 'c'), F("check", 'c'), F("true_offset", 'l'), F("surface", 'p', 0, 1006), F("offset", 'f'), F("scale", 'f'));
        T(61, "PARASURF", false, F("node_id", 'd'), F("attributes_features", 'p', 0, 1019), F("owner", 'p', 0, 1007), F("next", 'p', 0, 1006), F("previous", 'p', 0, 1006), F("geometric_owner", 'p', 0, 141), F("sense", 'c'), F("col", 'd'), F("row", 'd'), F("cps", 'p', 0, 69));
        T(62, "OBSOLETE_CPS", true, F("node_id", 'd'), F("attributes_features", 'p', 0, 1019), F("owner", 'p', 0, 1007), F("next", 'p', 0, 1006), F("previous", 'p', 0, 1006), F("geometric_owner", 'p', 0, 141), F("sense", 'c'), F("vertex_dimension", 'n'), F("col_count", 'n'), F("row_count", 'n'), F("u_order", 'n'), F("v_order", 'n'), F("patch", 'p', 1, 1026));
        T(63, "SILH_SURF", false, F("node_id", 'd'), F("attributes_features", 'p', 0, 1019), F("owner", 'p', 0, 1007), F("next", 'p', 0, 1006), F("previous", 'p', 0, 1006), F("geometric_owner", 'p', 0, 141), F("sense", 'c'), F("from_infinity", 'l'), F("surface", 'p', 0, 1006), F("eye", 'v'));
        T(64, "BASIC_PATCH", true, F("u_length", 'f'), F("v_length", 'f'), F("bezier_vertices", 'f', 1));
        T(65, "HULL", true, F("dimension", 'n'), F("plane_count", 'n'), F("corner_count", 'n'), F("vecs", 'v', 1));
        T(66, "BSPLINE_SURF", false, F("row_knots", 'p', 0, 44), F("col_knots", 'p', 0, 44), F("vertex_dimension", 'n'), F("col_count", 'd'), F("row_count", 'd'), F("u_order", 'n'), F("v_order", 'n'), F("bspline_vertices", 'p', 0, 45));
        T(67, "SWEPT_SURF", false, F("node_id", 'd'), F("attributes_features", 'p', 0, 1019), F("owner", 'p', 0, 1007), F("next", 'p', 0, 1006), F("previous", 'p', 0, 1006), F("geometric_owner", 'p', 0, 141), F("sense", 'c'), F("section", 'p', 0, 1008), F("sweep", 'v'), F("scale", 'f'));
        T(68, "SPUN_SURF", false, F("node_id", 'd'), F("attributes_features", 'p', 0, 1019), F("owner", 'p', 0, 1007), F("next", 'p', 0, 1006), F("previous", 'p', 0, 1006), F("geometric_owner", 'p', 0, 141), F("sense", 'c'), F("profile", 'p', 0, 1008), F("base", 'v'), F("axis", 'v'), F("start", 'v'), F("end", 'v'), F("start_param", 'f'), F("end_param", 'f'), F("x_axis", 'v'), F("scale", 'f'));
        T(69, "CPS", false, F("node_id", 'd'), F("attributes_features", 'p', 0, 1019), F("owner", 'p', 0, 1007), F("next", 'p', 0, 1006), F("previous", 'p', 0, 1006), F("geometric_owner", 'p', 0, 141), F("sense", 'c'), F("bezier", 'p', 0, 104), F("bspline", 'p', 0, 66));
        T(70, "LIST", false, F("node_id", 'd'), F("owner", 'p', 0, 1013), F("next", 'p', 0, 70), F("previous", 'p', 0, 70), F("list_type", 'd'), F("list_length", 'd'), F("block_length", 'd'), F("size_of_entry", 'd'), F("list_block", 'p', 0, 1012), F("finger_block", 'p', 0, 1012), F("finger_index", 'd'), F("notransmit", 'l'));
        T(71, "REAL_LIS_BLOCK", true, F("n_entries", 'd'), F("next_block", 'p', 0, 71), F("entries", 'f', 1));
        T(72, "INTEGER_LIS_BLOCK", true, F("n_entries", 'd'), F("next_block", 'p', 0, 72), F("entries", 'd', 1));
        T(73, "TAG_LIS_BLOCK", true, F("n_entries", 'd'), F("next_block", 'p', 0, 73), F("entries", 't', 1));
        T(74, "POINTER_LIS_BLOCK", true, F("n_entries", 'd'), F("next_block", 'p', 0, 74), F("entries", 'p', 1, 1001));
        T(78, "ATTRIB_CALLBACKS", false, F("split_fn", 'q'), F("merge_fn", 'q'), F("delete_fn", 'q'), F("copy_fn", 'q'), F("transmit_fn", 'q'), F("receive_fn", 'q'), F("split_on", 'l'), F("merge_on", 'l'), F("delete_on", 'l'), F("copy_on", 'l'), F("transmit_on", 'l'), F("receive_on", 'l'), F("callback_type", 'c'));
        T(79, "ATT_DEF_ID", true, F("string", 'c', 1));
        T(80, "ATTRIB_DEF", true, F("next", 'p', 0, 80), F("identifier", 'p', 0, 79), F("type_id", 'd'), F("actions", 'u', 8), F("field_names", 'p', 0, 99), F("legal_owners", 'l', 14), F("fields", 'u', 1));
        T(81, "ATTRIBUTE", true, F("node_id", 'd'), F("definition", 'p', 0, 80), F("owner", 'p', 0, 1015), F("next", 'p', 0, 1019), F("previous", 'p', 0, 1019), F("next_of_type", 'p', 0, 81), F("previous_of_type", 'p', 0, 81), F("fields", 'p', 1, 1018));
        T(82, "INT_VALUES", true, F("values", 'd', 1));
        T(83, "REAL_VALUES", true, F("values", 'f', 1));
        T(84, "CHAR_VALUES", true, F("values", 'c', 1));
        T(85, "POINT_VALUES", true, F("values", 'v', 1));
        T(86, "VECTOR_VALUES", true, F("values", 'v', 1));
        T(87, "AXIS_VALUES", true, F("values", 'v', 1));
        T(88, "TAG_VALUES", true, F("values", 't', 1));
        T(89, "DIRECTION_VALUES", true, F("values", 'v', 1));
        T(90, "FEATURE", false, F("node_id", 'd'), F("attributes_features", 'p', 0, 1019), F("owner", 'p', 0, 1005), F("next", 'p', 0, 1019), F("previous", 'p', 0, 1019), F("type", 'u'), F("first_member", 'p', 0, 91));
        T(91, "MEMBER_OF_FEATURE", false, F("dummy_node_id", 'd'), F("owning_feature", 'p', 0, 90), F("owner", 'p', 0, 1017), F("next", 'p', 0, 1019), F("previous", 'p', 0, 1019), F("next_member", 'p', 0, 91), F("previous_member", 'p', 0, 91));
        T(95, "POINTER_VALUES", true);
        T(96, "SHORT_VALUES", true, F("values", 'n', 1));
        T(97, "BOX_VALUES", true, F("values", 'b', 1));
        T(98, "UNICODE_VALUES", true, F("values", 'w', 1));
        T(99, "FIELD_NAMES", true, F("names", 'p', 1, 1037));
        T(100, "TRANSFORM", false, F("node_id", 'd'), F("owner", 'p', 0, 1023), F("next", 'p', 0, 100), F("previous", 'p', 0, 100), F("rotation_matrix", 'f', 9), F("translation_vector", 'v'), F("scale", 'f'), F("flag", 'd'), F("perspective_vector", 'v'));
        T(101, "WORLD", false, F("assembly", 'p', 0, 10), F("attribute", 'p', 0, 81), F("body", 'p', 0, 12), F("transform", 'p', 0, 100), F("surface", 'p', 0, 1006), F("curve", 'p', 0, 1008), F("point", 'p', 0, 29), F("alive", 'l'), F("attrib_def", 'p', 0, 80), F("highest_id", 'd'), F("current_id", 'd'));
        T(102, "KEY", true, F("string", 'c', 1));
        T(103, "BEZIER_CURVE", true, F("vertex_dimension", 'n'), F("segment_count", 'd'), F("order", 'n'), F("check", 'c'), F("segment", 'p', 1, 1025));
        T(104, "BEZIER_SURF", true, F("vertex_dimension", 'n'), F("col_count", 'd'), F("row_count", 'd'), F("u_order", 'n'), F("v_order", 'n'), F("check", 'c'), F("patch", 'p', 1, 1026));
        T(105, "SUPER_PATCH", false, F("split", 'f'), F("split_dir", 'c'), F("first", 'p', 0, 1026), F("last", 'p', 0, 1026), F("u_length", 'f'), F("v_length", 'f'));
        T(106, "EVENT_FILTER", false, F("face", 'l'), F("edge", 'l'), F("vertex", 'l'), F("loop", 'l'), F("shell", 'l'), F("region", 'l'), F("instance", 'l'), F("body", 'l'), F("assembly", 'l'), F("point", 'l'), F("curve", 'l'), F("surface", 'l'), F("transform", 'l'), F("feature", 'l'), F("att_def", 'l'));
        T(107, "BULLETIN_BOARD", false, F("create", 'p', 0, 106), F("delete", 'p', 0, 106), F("change", 'p', 0, 106), F("transform", 'p', 0, 106), F("split", 'p', 0, 106), F("merge", 'p', 0, 106), F("copy", 'p', 0, 106), F("transfer", 'p', 0, 106), F("change_attribute", 'p', 0, 106), F("event", 'p', 0, 70), F("entities", 'p', 0, 70), F("n_entities", 'p', 0, 70), F("entity_nt", 'p', 0, 70), F("entity_usfld", 'p', 0, 70));
        T(108, "UNSANITISED", true, F("bad_patch", 'p', 1, 64));
        T(109, "PBOX", false, F("type", 'c'), F("sense", 'c'), F("u_int", 'i'), F("v_int", 'i'));
        T(110, "SET_ELEMENT_TAG", false, F("next", 'p', 0, 110), F("forward", 'p', 0, 110), F("backward", 'p', 0, 110), F("class", 'd'), F("set", 'p', 0, 1021), F("node", 'p', 0, 1022));
        T(111, "FACE_SET", false, F("tag", 'p', 0, 110), F("next", 'p', 0, 111), F("class", 'd'), F("he_set", 'p', 0, 112), F("surfaces", 'p', 0, 1006));
        T(112, "HALFEDGE_SET", false, F("tag", 'p', 0, 110), F("next", 'p', 0, 112), F("previous", 'p', 0, 112), F("class", 'd'), F("fa_set", 'p', 0, 111), F("co_he_set", 'p', 0, 112));
        T(120, "PE_SURF", true, F("node_id", 'd'), F("attributes_features", 'p', 0, 1019), F("owner", 'p', 0, 1007), F("next", 'p', 0, 1006), F("previous", 'p', 0, 1006), F("geometric_owner", 'p', 0, 141), F("sense", 'c'), F("type", 'c'), F("data", 'p', 0, 1027), F("tf", 'p', 0, 100), F("internal_geom", 'p', 1, 1028));
        T(121, "INT_PE_DATA", false, F("geom_type", 'd'), F("real_array", 'p', 0, 83), F("int_array", 'p', 0, 82));
        T(122, "EXT_PE_DATA", true, F("key", 'p', 0, 102), F("real_array", 'p', 0, 83), F("int_array", 'p', 0, 82));
        T(123, "SU_PARAM_PROPS", false, F("uint", 'i'), F("vint", 'i'), F("u_start", 'c'), F("u_end", 'c'), F("v_start", 'c'), F("v_end", 'c'));
        T(124, "B_SURFACE", false, F("node_id", 'd'), F("attributes_features", 'p', 0, 1019), F("owner", 'p', 0, 1007), F("next", 'p', 0, 1006), F("previous", 'p', 0, 1006), F("geometric_owner", 'p', 0, 141), F("sense", 'c'), F("nurbs", 'p', 0, 126), F("data", 'p', 0, 125));
        T(125, "SURFACE_DATA", false, F("original_uint", 'i'), F("original_vint", 'i'), F("extended_uint", 'i'), F("extended_vint", 'i'), F("self_int", 'u'), F("original_u_start", 'c'), F("original_u_end", 'c'), F("original_v_start", 'c'), F("original_v_end", 'c'), F("extended_u_start", 'c'), F("extended_u_end", 'c'), F("extended_v_start", 'c'), F("extended_v_end", 'c'), F("analytic_form_type", 'c'), F("swept_form_type", 'c'), F("spun_form_type", 'c'), F("blend_form_type", 'c'), F("analytic_form", 'p', 0, 1030), F("swept_form", 'p', 0, 1031), F("spun_form", 'p', 0, 1032), F("blend_form", 'p', 0, 1033));
        T(126, "NURBS_SURF", false, F("u_periodic", 'l'), F("v_periodic", 'l'), F("u_degree", 'n'), F("v_degree", 'n'), F("n_u_vertices", 'd'), F("n_v_vertices", 'd'), F("u_knot_type", 'u'), F("v_knot_type", 'u'), F("n_u_knots", 'd'), F("n_v_knots", 'd'), F("rational", 'l'), F("u_closed", 'l'), F("v_closed", 'l'), F("surface_form", 'u'), F("vertex_dim", 'n'), F("bspline_vertices", 'p', 0, 45), F("u_knot_mult", 'p', 0, 127), F("v_knot_mult", 'p', 0, 127), F("u_knots", 'p', 0, 128), F("v_knots", 'p', 0, 128));
        T(127, "KNOT_MULT", true, F("mult", 'n', 1));
        T(128, "KNOT_SET", true, F("knots", 'f', 1));
        T(129, "KNOT_MULT_SUM", true, F("mult", 'd', 1));
        T(130, "PE_CURVE", true, F("node_id", 'd'), F("attributes_features", 'p', 0, 1019), F("owner", 'p', 0, 1010), F("next", 'p', 0, 1008), F("previous", 'p', 0, 1008), F("geometric_owner", 'p', 0, 141), F("sense", 'c'), F("type", 'c'), F("data", 'p', 0, 1027), F("tf", 'p', 0, 100), F("internal_geom", 'p', 1, 1028));
        T(131, "CU_PARAM_PROPS", false, F("tint", 'i'), F("t_start", 'c'), F("t_end", 'c'));
        T(132, "PCURVE", false, F("node_id", 'd'), F("attributes_features", 'p', 0, 1019), F("owner", 'p', 0, 1010), F("next", 'p', 0, 1008), F("previous", 'p', 0, 1008), F("geometric_owner", 'p', 0, 141), F("sense", 'c'), F("bsp_parms", 'l'), F("surface", 'p', 0, 1006), F("bspline", 'p', 0, 43));
        T(133, "TRIMMED_CURVE", false, F("node_id", 'd'), F("attributes_features", 'p', 0, 1019), F("owner", 'p', 0, 1010), F("next", 'p', 0, 1008), F("previous", 'p', 0, 1008), F("geometric_owner", 'p', 0, 141), F("sense", 'c'), F("basis_curve", 'p', 0, 1008), F("point_1", 'v'), F("point_2", 'v'), F("parm_1", 'f'), F("parm_2", 'f'));
        T(134, "B_CURVE", false, F("node_id", 'd'), F("attributes_features", 'p', 0, 1019), F("owner", 'p', 0, 1010), F("next", 'p', 0, 1008), F("previous", 'p', 0, 1008), F("geometric_owner", 'p', 0, 141), F("sense", 'c'), F("nurbs", 'p', 0, 136), F("data", 'p', 0, 135));
        T(135, "CURVE_DATA", false, F("self_int", 'u'), F("analytic_form", 'p', 0, 1036));
        T(136, "NURBS_CURVE", false, F("degree", 'n'), F("n_vertices", 'd'), F("vertex_dim", 'n'), F("n_knots", 'd'), F("knot_type", 'u'), F("periodic", 'l'), F("closed", 'l'), F("rational", 'l'), F("curve_form", 'u'), F("bspline_vertices", 'p', 0, 45), F("knot_mult", 'p', 0, 127), F("knots", 'p', 0, 128));
        T(137, "SP_CURVE", false, F("node_id", 'd'), F("attributes_features", 'p', 0, 1019), F("owner", 'p', 0, 1010), F("next", 'p', 0, 1008), F("previous", 'p', 0, 1008), F("geometric_owner", 'p', 0, 141), F("sense", 'c'), F("surface", 'p', 0, 1006), F("b_curve", 'p', 0, 134), F("original", 'p', 0, 1008), F("tolerance_to_original", 'f'));
        T(138, "SP_CHART", true, F("chart_count", 'd'), F("chordal_error", 'f'), F("angular_error", 'f'), F("hvec", 'h', 1));
        T(139, "CU_TREE", false, F("n_pvecs", 'd'), F("n_branches", 'd'), F("n_leaves", 'd'), F("chordal_error", 'f'), F("angular_error", 'f'), F("regular_pvecs", 'p', 0, 85), F("split_parms", 'p', 0, 83), F("branch_parents", 'p', 0, 96), F("branch_lo_children", 'p', 0, 96), F("branch_hi_children", 'p', 0, 96), F("leaf_parents", 'p', 0, 96));
        T(141, "GEOMETRIC_OWNER", false, F("owner", 'p', 0, 1003), F("next", 'p', 0, 141), F("previous", 'p', 0, 141), F("shared_geometry", 'p', 0, 1003));
        T(150, "PLANE_FORM", false, F("pvec", 'v'), F("normal", 'v'));
        T(151, "CYLINDER_FORM", false, F("pvec", 'v'), F("axis", 'v'), F("radius", 'f'), F("sense", 'c'));
        T(152, "CONE_FORM", false, F("pvec", 'v'), F("axis", 'v'), F("radius", 'f'), F("sin_half_angle", 'f'), F("cos_half_angle", 'f'), F("sense", 'c'));
        T(153, "SPHERE_FORM", false, F("centre", 'v'), F("radius", 'f'), F("sense", 'c'));
        T(154, "TORUS_FORM", false, F("centre", 'v'), F("axis", 'v'), F("major_radius", 'f'), F("minor_radius", 'f'), F("sense", 'c'));
        T(155, "SWEPT_FORM", false, F("sweep", 'v'), F("subtype", 'c'));
        T(156, "SWEPT_UV_FORM", false, F("sweep_u", 'v'), F("sweep_v", 'v'), F("subtype_u", 'c'), F("subtype_v", 'c'));
        T(157, "SPUN_FORM", false, F("base", 'v'), F("axis", 'v'), F("subtype", 'c'));
        T(158, "VAR_RADIUS_PIPE_FORM", false, F("max_radius", 'f'), F("min_radius", 'f'), F("max_radius_bdry", 'c'), F("min_radius_bdry", 'c'));
        T(159, "SU_DEGENERACY", false, F("pvec", 'v'), F("normal", 'v'), F("u_range", 'i'), F("v_range", 'i'), F("type", 'd'), F("next", 'p', 0, 159));
        T(160, "SU_TREE", false, F("n_u_pvecs", 'd'), F("n_v_pvecs", 'd'), F("n_branches", 'd'), F("n_leaves", 'd'), F("distance_error", 'f'), F("angular_error", 'f'), F("regular_pvecs", 'p', 0, 85), F("split_parms", 'p', 0, 83), F("split_dirs", 'p', 0, 84), F("branch_parents", 'p', 0, 96), F("branch_lo_children", 'p', 0, 96), F("branch_hi_children", 'p', 0, 96), F("leaf_parents", 'p', 0, 96), F("leaf_boxes", 'p', 0, 97), F("leaf_pvecs", 'p', 0, 85), F("leaf_u_lo", 'p', 0, 83), F("leaf_u_hi", 'p', 0, 83), F("leaf_v_lo", 'p', 0, 83), F("leaf_v_hi", 'p', 0, 83), F("bucket_chars", 'p', 0, 84), F("bucket_shorts", 'p', 0, 96), F("bucket_ints", 'p', 0, 82), F("bucket_reals", 'p', 0, 83), F("leaf_u_splits", 'p', 0, 96), F("leaf_v_splits", 'p', 0, 96));
        T(161, "POLYNOMIAL_PATCHES", true, F("n_patches", 'd'), F("patch", 'p', 1, 162));
        T(162, "POLYNOMIAL_PATCH", false, F("last_used", 'd'), F("n_derivatives", 'd'), F("triangular", 'l'), F("u_knot_index", 'd'), F("v_knot_index", 'd'), F("uint", 'i'), F("vint", 'i'), F("u_scale", 'f'), F("v_scale", 'f'), F("u_on_right", 'l'), F("v_on_right", 'l'), F("C", 'p', 0, 86), F("Cdu", 'p', 0, 86), F("Cdv", 'p', 0, 86), F("Cd2u", 'p', 0, 86), F("Cdudv", 'p', 0, 86), F("Cd2v", 'p', 0, 86), F("Cd2udv", 'p', 0, 86), F("Cd2vdu", 'p', 0, 86), F("Cd2ud2v", 'p', 0, 86), F("W", 'p', 0, 83), F("Wdu", 'p', 0, 83), F("Wdv", 'p', 0, 83), F("Wd2u", 'p', 0, 83), F("Wdudv", 'p', 0, 83), F("Wd2v", 'p', 0, 83), F("Wd2udv", 'p', 0, 83), F("Wd2vdu", 'p', 0, 83), F("Wd2ud2v", 'p', 0, 83));
        T(163, "HELIX_SU_FORM", false, F("axis_pt", 'v'), F("axis_dir", 'v'), F("hand", 'c'), F("turns", 'i'), F("pitch", 'f'), F("gap", 'f'), F("tol", 'f'));
        T(168, "CHAIN_HEAD", false, F("dummy", 'c'));
        T(169, "PMARK_ARRAY_INTERNAL", true, F("pmarks", 'p', 1, 4));
        T(170, "PMARK_ARRAY", false, F("length", 'd'), F("size", 'd'), F("array", 'p', 0, 169));
        T(171, "ROLL_DATA", false, F("current_partition", 'p', 0, 3), F("bulletin_board", 'p', 0, 107), F("linear_prec", 'f'), F("angular_prec", 'f'), F("receive_user_fld", 'l'), F("local_check_on", 'l'), F("data_check_on", 'l'), F("self_int_check_on", 'l'), F("generated_surfs_on", 'l'), F("general_topology_on", 'l'), F("keep_edge_curve_on", 'l'), F("bulletin_board_on", 'd'), F("binary_text", 'd'), F("snapshot_binary_text", 'd'), F("continuity_check", 'd'));
        T(172, "SESSION_DATA", false, F("attrib_def", 'p', 0, 80), F("parameter_check_on", 'l'), F("journal_on", 'l'), F("roll_forward", 'l'), F("journal_open", 'd'), F("rollback_size", 'd'), F("tag_limit", 'd'));
        T(180, "POLYNOMIAL_SEGMENTS", true, F("n_segments", 'd'), F("segment", 'p', 1, 181));
        T(181, "POLYNOMIAL_SEGMENT", false, F("last_used", 'd'), F("n_derivatives", 'd'), F("knot_index", 'd'), F("tint", 'i'), F("scale", 'f'), F("on_right", 'l'), F("C", 'p', 0, 86), F("Cdt", 'p', 0, 86), F("Cd2t", 'p', 0, 86), F("W", 'p', 0, 83), F("Wdt", 'p', 0, 83), F("Wd2t", 'p', 0, 83));
        T(182, "HVEC_ARRAY", true, F("hvec", 'h', 1));
        T(183, "HVEC_ORDERING", true, F("order", 'd', 1));
        T(184, "HELIX_CU_FORM", false, F("axis_pt", 'v'), F("axis_dir", 'v'), F("point", 'v'), F("hand", 'c'), F("turns", 'i'), F("pitch", 'f'), F("tol", 'f'));
        return d;
    }
}
