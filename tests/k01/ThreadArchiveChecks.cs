using OpenTK.Mathematics;
using Parametric3D.Model;
using Parametric3D.Model.Features;
using Parametric3D.Model.Params;
using Parametric3D.Model.Threads;

static partial class Program
{
    // Synthetic parser data, NOT a standard-compliant thread catalogue.
    static void ThreadArchiveChecks()
    {
        Check("THREAD-ARCHIVE-JSON", () =>
        {
            var rows = ThreadDb.ParseJson("""
                {"threads":[{"family":"TEST","designation":"SYNTHETIC","series":"test",
                "d_major":8,"P":1,"starts":2,"hand":"LH",
                "sweep_ext":{"profile_zr":[[-0.4,4.1],[0,3.4],[0.4,4.1]],"start_radius":4},
                "sweep_int":{"profile_zr":[[-0.4,3.4],[0,4.1],[0.4,3.4]],"start_radius":3.5}}]}
                """);
            Require(rows.Count == 1, "synthetic table row missing");
            var row = rows[0];
            Require(row.Id == "TEST|SYNTHETIC|test", "stable ID");
            Require(row.P == 1 && row.Starts == 2 && row.Lead == 2 && row.StdLeft, "pitch/lead/hand");
            Require(row.ExtZr.Length == 3 && row.IntZr.Length == 3, "profiles");
        });
        Check("THREAD-ARCHIVE-INCOMPLETE-ROW", () =>
        {
            Require(ThreadDb.ParseJson("{}").Count == 0, "missing table accepted");
            Require(ThreadDb.ParseJson("""{"threads":[{"d_major":8,"P":1}]}""").Count == 0,
                "row without profiles accepted");
        });
        Check("THREAD-ARCHIVE-ROUNDTRIP", () =>
        {
            var part = new Part("thread archive");
            var feature = new CosmeticThreadFeature
            {
                TypeId = "TEST|SYNTHETIC|test", Cosmetic = false, Pitch = new ValueRef(1.25),
                Length = new ValueRef(7), StartAt = new Vector3d(1, 2, 3),
                EndAt = new Vector3d(4, 5, 6), Mode = ThreadLength.UpTo,
                SmoothExit = true, LeftHand = true
            };
            feature.OnFaces.Add(new Vector3d(2, 3, 4));
            feature.OnFaceNames.Add("test-face");
            part.Features.Add(feature);
            var loaded = DocumentFile.Load(DocumentFile.Save(part)).Part;
            var saved = loaded.Features.OfType<CosmeticThreadFeature>().Single();
            Require(saved.TypeId == feature.TypeId && !saved.Cosmetic, "type/cosmetic persistence");
            Require(saved.Mode == ThreadLength.UpTo && saved.SmoothExit && saved.LeftHand, "options persistence");
            Require(saved.StartAt == feature.StartAt && saved.EndAt == feature.EndAt, "references persistence");
            Require(saved.OnFaces.SequenceEqual(feature.OnFaces) && saved.OnFaceNames.SequenceEqual(feature.OnFaceNames),
                "face persistence");
            Require(saved.Pitch.Resolve(new ParameterTable()) == 1.25
                && saved.Length.Resolve(new ParameterTable()) == 7, "dimensions persistence");
        });
        Check("THREAD-ARCHIVE-DEFAULTS", () =>
        {
            var feature = new CosmeticThreadFeature();
            Require(feature.Cosmetic && feature.TypeId == null && feature.Mode == ThreadLength.Blind,
                "untyped cosmetic default");
        });
        Check("THREAD-ARCHIVE-CATALOG-PARALLEL", () =>
        {
            Parallel.For(0, 128, _ =>
            {
                var all = ThreadDb.All;
                if (all.Count > 0)
                    Require(ReferenceEquals(ThreadDb.Find(all[0].Id), all[0]), "catalog/index mismatch");
                else
                    Require(ThreadDb.LoadError != null, "missing catalog diagnostic");
            });
        });
        Check("THREAD-ARCHIVE-CATALOG-READONLY", () =>
        {
            var list = ThreadDb.All as IList<ThreadSpec>;
            Require(list != null && list.IsReadOnly, "catalog list exposes mutation");
            bool rejected = false;
            try { list!.Clear(); } catch (NotSupportedException) { rejected = true; }
            Require(rejected, "catalog list can be cleared");
        });
    }
}
