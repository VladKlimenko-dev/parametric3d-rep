# Корпус файлов SolidWorks для проверки импорта SLDPRT

Отдельная ветка без общей истории с `main`: файлы большие (сотни МБ) и не должны утяжелять клон основной ветки.
Прогон `state-probe` (задания `k1xx_corpus*`) скачивает эту ветку и разбирает каталоги командой
`K01Checks sldprt <каталог>`.

- `sldprt1/` — 136 деталей McMaster-Carr (архив пользователя `sldprt1.zip`, 08.11.2026).
- `sldprt2/` — 89 деталей McMaster-Carr (архив `sldprt2.zip`, 08.11.2026).
- `sldprt3/` — 137 деталей McMaster-Carr (архив `sldprt3.zip`, 08.11.2026).
- `sldprt4/` — 50 деталей McMaster-Carr (архив `sldprt4.zip`, 08.11.2026).
