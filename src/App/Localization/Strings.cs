namespace Parametric3D.App;

/// <summary>
/// ПЕРЕВОД ИНТЕРФЕЙСА: русская строка из кода → английская. Ключ — ровно та строка, что передаётся в L() (с {0} для
/// подстановок). Нет записи — показывается русская (так недостающее сразу видно при переключении языка).
/// </summary>
public static class Strings
{
    public static readonly Dictionary<string, string> En = new()
    {
        // ---------------------------------------------------------------- общие
        ["ОК"] = "OK", ["Отмена"] = "Cancel", ["Готово"] = "Done", ["Очистить"] = "Clear", ["да"] = "yes", ["нет"] = "no",
        ["мм"] = "mm", ["шт"] = "pcs", ["см³"] = "cm³", ["см²"] = "cm²", ["кг"] = "kg",
        ["Внутренняя ошибка: "] = "Internal error: ",
        ["Нет детали"] = "No part",
        ["Тест"] = "Test", ["Тест: замер скорости и отчёт"] = "Test: speed measurement and report", ["Начать"] = "Start", ["Повторить"] = "Run again",
        ["Остановить"] = "Stop", ["Закрыть"] = "Close", ["Копировать отчёт"] = "Copy report", ["Открыть папку"] = "Open folder",
        ["Отчёт: {0}"] = "Report: {0}", ["Отчёт скопирован в буфер обмена."] = "The report is copied to the clipboard.",
        ["Приложение построит детали и замерит время: каждую операцию отдельно, реалистичные детали (полное перестроение, правка, предпросмотр, память), 100 отверстий, долгую сессию, файл и экспорт, затем вид на OpenGL этого компьютера (кадры в секунду во всех стилях, выбор под курсором) и сценарии интерфейса. Займёт 2–5 минут; во время теста не трогайте мышь над окном. Документ откроется снова по окончании. Отчёт сохраняется в файл — пришлите его."] =
            "The application will build parts and measure time: each operation separately, realistic parts (full rebuild, edit, preview, memory), 100 holes, a long session, file and export, then the view on this computer's OpenGL (frames per second in every style, picking under the cursor) and UI scenarios. It takes 2–5 minutes; keep the mouse off the window meanwhile. Your document is reopened at the end. The report is saved to a file — please send it.",

        // ---------------------------------------------------------------- группы команд
        ["Файл"] = "File", ["Правка"] = "Edit", ["Вид"] = "View", ["Модель"] = "Model", ["Эскиз"] = "Sketch", ["Приложение"] = "Application",

        // ---------------------------------------------------------------- файл
        ["Новая"] = "New", ["Новая деталь"] = "New part", ["Пробная деталь (куб 100)"] = "Sample part (cube 100)",
        ["Открыть"] = "Open", ["Открыть…"] = "Open…", ["Открыть: "] = "Open: ", ["Сохранить"] = "Save", ["Сохранить как…"] = "Save as…",
        ["Импорт"] = "Import", ["Импорт (STEP, IGES)…"] = "Import (STEP, IGES)…", ["Экспорт"] = "Export", ["Экспорт (STEP, STL, OBJ)…"] = "Export (STEP, STL, OBJ)…",
        ["Выход"] = "Exit", ["Последние файлы"] = "Recent files", ["Сохранить деталь"] = "Save part", ["Деталь Parametric3D"] = "Parametric3D part",
        ["Сохранено: {0}"] = "Saved: {0}", ["Не сохранять"] = "Don't save", ["Несохранённые изменения"] = "Unsaved changes",
        ["Сохранить изменения в «{0}»?"] = "Save changes to “{0}”?", ["Экспорт: в детали ещё нет тела."] = "Export: the part has no body yet.",
        ["STEP точный (B-rep)"] = "STEP exact (B-rep)",

        // ---------------------------------------------------------------- правка
        ["Отменить"] = "Undo", ["Вернуть"] = "Redo", ["Журнал"] = "History", ["Журнал отмены"] = "Undo history", ["Журнал пуст"] = "History is empty",
        ["Отменить этот шаг"] = "Undo this step", ["Отменить {0} шагов — до состояния перед этим"] = "Undo {0} steps — back to the state before this one",
        ["Удалить"] = "Delete", ["Удалить «{0}»"] = "Delete “{0}”", ["Удалить ({0})"] = "Delete ({0})",
        ["Изменить"] = "Edit", ["Изменить операцию"] = "Edit feature", ["Изменить «{0}»"] = "Edit “{0}”",
        ["Изменить эскиз"] = "Edit sketch", ["Изменить эскиз «{0}»"] = "Edit sketch “{0}”",
        ["Подавить"] = "Suppress", ["Подавить / вернуть"] = "Suppress / resume", ["Снять выбор"] = "Clear selection",
        ["Правка этой операции в панели пока не поддерживается"] = "Editing this feature in the panel is not supported yet",

        // ---------------------------------------------------------------- вид
        ["Вид спереди"] = "Front view", ["Вид сзади"] = "Back view", ["Вид слева"] = "Left view", ["Вид справа"] = "Right view",
        ["Вид сверху"] = "Top view", ["Вид снизу"] = "Bottom view", ["Домой"] = "Home", ["Исходный вид (изометрия)"] = "Home view (isometric)",
        ["Вписать"] = "Fit", ["Вписать всё"] = "Fit all", ["Персп."] = "Persp.", ["Орто"] = "Ortho",
        ["Проекция: перспектива / ортогональ"] = "Projection: perspective / orthographic",
        ["Стиль отображения"] = "Display style", ["Тонированный"] = "Shaded", ["Тонированный с рёбрами"] = "Shaded with edges",
        ["Каркас"] = "Wireframe", ["Невидимые линии штрихом"] = "Hidden lines dashed", ["Прозрачный"] = "Transparent",
        ["Скрыть"] = "Hide", ["Изолировать"] = "Isolate", ["Показать всё"] = "Show all",
        ["Опорные"] = "Datums", ["Опорные элементы: показать / скрыть"] = "Datums: show / hide", ["Что показывать"] = "What to show",
        ["ПОКАЗЫВАТЬ"] = "SHOW", ["Показывать: "] = "Show: ",
        ["Плоскости"] = "Planes", ["Оси"] = "Axes", ["Начало координат"] = "Origin", ["Точки"] = "Points", ["Линии"] = "Lines",
        ["Вершины"] = "Vertices", ["Осевые линии"] = "Centerlines", ["Эскизы"] = "Sketches", ["Размеры"] = "Dimensions",
        ["Обозначения"] = "Annotations", ["Измерения"] = "Measurements",
        ["Показывать: Плоскости"] = "Show: Planes", ["Показывать: Оси"] = "Show: Axes", ["Показывать: Начало координат"] = "Show: Origin",
        ["Показывать: Точки"] = "Show: Points", ["Показывать: Линии"] = "Show: Lines", ["Показывать: Вершины"] = "Show: Vertices",
        ["Показывать: Осевые линии"] = "Show: Centerlines", ["Показывать: Эскизы"] = "Show: Sketches", ["Показывать: Размеры"] = "Show: Dimensions",
        ["Показывать: Обозначения"] = "Show: Annotations", ["Показывать: Измерения"] = "Show: Measurements",
        ["ПРАВО"] = "RIGHT", ["ЛЕВО"] = "LEFT", ["ВЕРХ"] = "TOP", ["НИЗ"] = "BOTTOM", ["ПЕРЕД"] = "FRONT", ["ЗАД"] = "BACK",

        // ---------------------------------------------------------------- приложение
        ["Тема: тёмная / светлая"] = "Theme: dark / light", ["Язык: русский / английский"] = "Language: Russian / English",
        ["Свойства"] = "Properties", ["Параметры"] = "Parameters", ["Палитра команд"] = "Command palette",
        ["Свернуть дерево"] = "Collapse tree", ["Развернуть дерево"] = "Expand tree",
        ["ДЕРЕВО МОДЕЛИ"] = "MODEL TREE", ["Найти команду…"] = "Find a command…",
        ["↑↓ — выбор, Enter — выполнить, Esc — закрыть. Поиск по-русски и по-английски."] = "↑↓ — select, Enter — run, Esc — close. Search in English or Russian.",

        // ---------------------------------------------------------------- операции
        ["ОПЕРАЦИИ"] = "FEATURES", ["Операция"] = "Feature", ["Деталь"] = "Part", ["Сборка"] = "Assembly",
        ["Выдавить"] = "Extrude", ["Выдавливание"] = "Extrude", ["Вырез"] = "Cut", ["Вырез выдавливанием"] = "Extruded cut",
        ["Вращение"] = "Revolve", ["Отверстие"] = "Hole", ["Скругление"] = "Round", ["Фаска"] = "Chamfer",
        ["Скругл. вершины"] = "Vertex round", ["Скругление вершины"] = "Vertex round", ["Фаска угла"] = "Corner chamfer",
        ["Оболочка"] = "Shell", ["Плоскость"] = "Plane", ["Опорная плоскость"] = "Datum plane", ["Зеркало"] = "Mirror",
        ["Массив"] = "Pattern", ["Линейный массив"] = "Linear pattern", ["Кр. массив"] = "Circ. pattern", ["Круговой массив"] = "Circular pattern",
        ["Протяжка"] = "Sweep", ["Переход"] = "Blend", ["Уклон"] = "Draft", ["Ребро жёсткости"] = "Rib",
        ["Начать эскиз"] = "Start sketch", ["Эскиз «{0}»"] = "Sketch “{0}”", ["Эскиз{0}"] = "Sketch{0}", ["Плоскость{0}"] = "Plane{0}",
        ["Деталь «{0}»"] = "Part “{0}”", ["Сборка «{0}»"] = "Assembly “{0}”", ["Документ «{0}»"] = "Document “{0}”",
        ["Тело"] = "Body", ["Добавить"] = "Add", ["Вырезать"] = "Cut", ["Глубина до"] = "Depth to", ["На расстояние"] = "Blind", ["Насквозь"] = "Through all",
        ["Глубина"] = "Depth", ["Обратное направление"] = "Flip direction", ["Симметрично"] = "Symmetric", ["Угол"] = "Angle",
        ["Ось"] = "Axis", ["Без оси — осевая линия эскиза."] = "No axis — the sketch centerline is used.",
        ["Рёбра"] = "Edges", ["Радиус"] = "Radius", ["Размер"] = "Size", ["Вершина"] = "Vertex",
        ["Открытые грани"] = "Open faces", ["Толщина"] = "Thickness", ["Отрицательная толщина — стенка наружу."] = "A negative thickness puts the wall outside.",
        ["Грань входа"] = "Entry face", ["Без точек — отверстие в середине грани."] = "No points — the hole is placed in the middle of the face.",
        ["Вид"] = "View", ["Простое"] = "Simple", ["С цекованием"] = "Counterbore", ["С зенковкой"] = "Countersink",
        ["Диаметр"] = "Diameter", ["Диаметр головки"] = "Head diameter", ["Глубина головки"] = "Head depth",
        ["Плоскость зеркала"] = "Mirror plane", ["Направление"] = "Direction", ["Шаг"] = "Spacing", ["Количество"] = "Count",
        ["Без оси — ось Z через начало координат."] = "No axis — the Z axis through the origin.",
        ["От плоскости"] = "From plane", ["Смещение"] = "Offset",
        ["Выберите опорную плоскость (в виде или в дереве) или плоскую грань тела."] = "Select a datum plane (in the view or the tree) or a planar face of the body.",
        ["Выберите плоскость или плоскую грань"] = "Select a plane or a planar face",
        ["Выберите: {0}"] = "Select: {0}", ["выберите в виде"] = "select in the view", ["выбрано: {0}"] = "selected: {0}",
        ["как было ({0})"] = "as before ({0})", ["не задано"] = "not set",
        ["Число или формула с параметрами; ↑↓ — шаг 1, Shift — 10, Ctrl — 0.1; Enter — применить"] =
            "A number or a formula with parameters; ↑↓ — step 1, Shift — 10, Ctrl — 0.1; Enter — apply",
        ["Выберите грань, ребро, вершину, плоскость или эскиз — здесь появятся подходящие операции"] =
            "Select a face, edge, vertex, plane or sketch — matching operations will appear here",

        // ---------------------------------------------------------------- эскиз
        ["ЭСКИЗ"] = "SKETCH", ["Отрезок"] = "Line", ["Прямоуг."] = "Rectangle", ["Прямоугольник"] = "Rectangle",
        ["Окружн."] = "Circle", ["Окружность"] = "Circle", ["Окр. 3т"] = "Circle 3pt", ["Окружность по 3 точкам"] = "Circle by 3 points",
        ["Дуга"] = "Arc", ["Дуга по 3 точкам"] = "Arc by 3 points", ["Дуга ц."] = "Center arc", ["Дуга от центра"] = "Arc by center",
        ["Эллипс"] = "Ellipse", ["Сплайн"] = "Spline", ["Точка"] = "Point", ["Осевая"] = "Centerline", ["Осевая линия"] = "Centerline",
        ["Вспом."] = "Constr.", ["Вспомогательная геометрия"] = "Construction geometry", ["Сетка"] = "Grid", ["Привязка к сетке"] = "Snap to grid",
        ["Закончить эскиз"] = "Finish sketch", ["Отменить эскиз"] = "Cancel sketch",
        ["Выберите инструмент слева; Ctrl+Enter — закончить эскиз"] = "Pick a tool on the left; Ctrl+Enter — finish the sketch",
        ["Щёлкните первую точку"] = "Click the first point", ["Щёлкните, чтобы поставить точку"] = "Click to place a point",
        ["Щёлкните начало отрезка"] = "Click the start of the line",
        ["Щёлкните конец; Enter или правая кнопка — закончить цепочку"] = "Click the end; Enter or right click finishes the chain",
        ["Щёлкните первый угол"] = "Click the first corner", ["Щёлкните противоположный угол"] = "Click the opposite corner",
        ["Щёлкните центр"] = "Click the center", ["Щёлкните точку на окружности"] = "Click a point on the circle",
        ["Щёлкните точку {0} из 3"] = "Click point {0} of 3",
        ["Щёлкните начало дуги"] = "Click the start of the arc", ["Щёлкните конец дуги"] = "Click the end of the arc", ["Щёлкните точку на дуге"] = "Click a point on the arc",
        ["Щёлкните конец большой оси"] = "Click the end of the major axis", ["Щёлкните точку малой оси"] = "Click a point of the minor axis",
        ["Щёлкайте точки сплайна; Enter или правая кнопка — готово"] = "Click spline points; Enter or right click — done",

        // ---------------------------------------------------------------- свойства и состояние
        ["ДЕТАЛЬ"] = "PART", ["ВЫБРАНО"] = "SELECTED", ["Выбрано"] = "Selected", ["Выбрано: "] = "Selected: ", ["Под курсором: "] = "Under cursor: ",
        ["Имя"] = "Name", ["Материал"] = "Material", ["Тел"] = "Bodies", ["Объём"] = "Volume", ["Площадь"] = "Area", ["Масса"] = "Mass",
        ["Операций"] = "Features", ["Параметров"] = "Parameters", ["Описание"] = "Description", ["Ошибка"] = "Error",
        ["Перестроение…"] = "Rebuilding…", ["Требуют внимания: {0}"] = "Need attention: {0}",
        ["Грань"] = "Face", ["Ребро"] = "Edge", ["Кривая"] = "Curve", ["Линия"] = "Line", ["Связь"] = "Constraint", ["Элемент"] = "Item",
        ["Грань #{0}"] = "Face #{0}",
        ["плоская"] = "planar", ["цилиндр"] = "cylinder", ["конус"] = "cone", ["шар"] = "sphere", ["тор"] = "torus", ["свободная"] = "freeform",
        ["прямое"] = "straight", ["дуга"] = "arc", ["эллипс"] = "ellipse", ["кривое"] = "curved",

        // ---------------------------------------------------------------- параметры
        ["Искать в:"] = "Look in:", ["Тип"] = "Type", ["Значение или формула"] = "Value or formula", ["Единица"] = "Unit", ["Результат"] = "Result",
        ["Число / формула"] = "Number / formula", ["Целое"] = "Integer", ["Логическое"] = "Boolean", ["Строка"] = "String",
        ["Пустое имя параметра"] = "Empty parameter name", ["Имя «{0}» повторяется"] = "The name “{0}” is repeated", ["имя повторяется"] = "repeated name",
        ["Параметры документа видны во всех сборках и деталях."] = "Document parameters are visible in all assemblies and parts.",
        ["Параметры сборки видны в её деталях; имя, которого здесь нет, ищется в документе."] =
            "Assembly parameters are visible in its parts; a name missing here is looked up in the document.",
        ["Параметры детали видны только в ней; имя, которого здесь нет, ищется в сборке и документе."] =
            "Part parameters are visible only in the part; a name missing here is looked up in the assembly and the document.",
    };
}
