# Автономные зависимости стендов проверок

Нужны только для сборки стендов (`tests/`) без доступа к NuGet: `-p:LocalDeps=<этот каталог>` (см. `tests/Directory.Build.props`).
Приложение (`src/Parametric3D.csproj`, Windows) берёт OpenTK из NuGet как обычно.

- `OTKMath/` — исходники OpenTK.Mathematics из OpenTK 4.9.4. OpenTK распространяется по лицензии MIT
  (https://github.com/opentk/opentk); при распространении поставки приложить текст лицензии из репозитория OpenTK.
- `SDCStub/` — заглушка System.Drawing.Common для сборки ядра без Windows (собственный код проекта).
- `NuGet.Config` — пустой список источников: сборка не обращается к сети.
