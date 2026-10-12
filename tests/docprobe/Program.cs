using Parametric3D.Model;
var p = new Part("d"); TestGeometry.AddCube(p, 60); p.Rebuild(null);
Console.WriteLine($"orig volume {p.Volume} err {string.Join(";", p.Features.Select(f => f.Error))}");
var json = DocumentFile.Save(p);
var (q, pars) = DocumentFile.Load(json); q.Rebuild(pars);
Console.WriteLine($"reloaded volume {q.Volume} err {string.Join(";", q.Features.Select(f => f.Error))}");
File.WriteAllText("/tmp/doc.json", json);
