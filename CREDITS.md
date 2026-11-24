# Авторство GameHelper2 DevEd

| Источник | Подтверждённое авторство и назначение |
| --- | --- |
| [Gordin/GameHelper2](https://github.com/Gordin/GameHelper2) | Gordin и участники истории проекта, включая Lafko. Основа локального оверлея, структуры игры, загрузчик и набор встроенных плагинов. Точная ревизия закреплена в `dev/sources.json`. |
| [yokkenUA/McpBridge](https://github.com/yokkenUA/McpBridge) и [yokkenUA/RunecraftHelper](https://github.com/yokkenUA/RunecraftHelper) | yokkenUA. Локально подключаемый MCP и чтение рун. Диагностическая работа по Atlas2 также отмечена в исходных комментариях локальной ветки. |
| [ExFy0/GameHelper2-Offset-Recovery-Framework](https://github.com/ExFy0/GameHelper2-Offset-Recovery-Framework) | ExFy0. Ghidra-скрипт установочной ветки восстановления. |
| [MordWraith/PlayerBuffBar](https://github.com/MordWraith/PlayerBuffBar) | MordWraith. Исторический плагин в локальном дереве, исключён из пакета DevEd согласно запросу пользователя. |
| [gahehe/GameHelper2Fork](https://github.com/gahehe/GameHelper2Fork), [MordWraith/Gamehelper](https://github.com/MordWraith/Gamehelper), [TahiKari/ExileCenter](https://github.com/TahiKari/ExileCenter) | Соответствующие сопровождающие и участники этих репозиториев. Ревизии в `dev/sources.json` использованы для технической сверки. Отдельное копирование их дерева в GitHub-импорт не подтверждено и не выполнялось. |
| GameHelper2 DevEd | Разработка под руководством MatveyTomilov с помощью Codex. Собственные части включают ExchangeTrader, тесты и фиксацию наблюдений, инструменты проверок структур и сигнатур, сценарии локальной сборки, упаковки, отката и Upstream Sync. |

Первый коммит фиксирует импорт существующей локальной разработки. Сведения об исходных авторах компонентов сохранены в локальном дереве и в [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).

## Авторы в истории закреплённого хоста

Имена приведены по Git-истории соответствующих путей на ревизии `3f1b541`. Для Atlas2 отдельно сохранена атрибуция yokkenUA за исследование строк Ritual в исходном комментарии. [Закреплённые исходники](https://github.com/Gordin/GameHelper2/tree/3f1b541cb7122ab4a710529c7780a85a75b1fde7)

| Компоненты | Авторы коммитов |
| --- | --- |
| Ядро `GameHelper` | Gordin, Lafko, Martin Fuchsberger, SmashPhoenix272 |
| `GameOffsets` | Gordin, Lafko |
| Atlas2, LootValue, RitualWispAlert | Gordin |
| AutoHotKeyTrigger, HealthBars, PreloadAlert, Radar | Gordin, Lafko |

ExchangeTrader и проверки к нему сохранены из локальной разработки DevEd. Прежний AutoCheliks использовался как источник для изучения подходов к торговле. Снимок его репозитория не входит в импорт. [AutoCheliks](https://github.com/MatveyTomilov/AutoCheliks)
