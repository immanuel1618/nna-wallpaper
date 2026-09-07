# NNA UI: дизайн-система v3

Источник истины: `H:\brand\tokens-v3.css` (вне репозитория, брендбук NNA1618 BRAND CODE v3).
В репозитории: `ui/tokens.css` (точная копия + алиасы для старых имён переменных, которыми
пользуются `wallpaper/*.css`, `settings/design-system.css`, `topbar/bar.css`).

Ориентир интерфейса: macOS (System Settings, menu bar), но палитра и шрифты только из
токенов v3. Никаких эмодзи, восклицательных знаков, длинных тире, курсива, цветов вне
палитры (кроме rgba-вариантов палитры для прозрачности), готовых иконочных наборов.

## Палитра

Монохром несёт всю работу, акценты: точечно, не больше 8% площади:

| Токен | HEX | Роль |
|---|---|---|
| `--base` | `#0B0B0B` | основа экрана |
| `--surface` | `#161616` | панели, карточки, поля ввода |
| `--slate` | `#434343` | линии, рамки, выключенные состояния: текстом не бывает |
| `--steel` | `#808080` | метки, подписи, служебные строки |
| `--ash` | `#C8C8C8` | основной текст на тёмном |
| `--white` | `#FFFFFF` | заголовки, ударные строки, активное состояние |
| `--blood` | `#5E1119` | плашка, заливка поля |
| `--signal` | `#8C1A25` | засечка, подчёркивание активного, точка состояния |
| `--chrome-flat` | `#B8BDC2` | плоская подмена хрома, элементы < 42px |
| `--chrome-gradient` | linear-gradient(...) | хром на элементах ≥ 42px, только на Base |

Правило: **цвета только из палитры**. Тест `ui/tests/palette.test.mjs` сканирует все `.css`
в `ui/`, `wallpaper/nna-brand.css`, `wallpaper/nna-blocks.css`, `wallpaper/layout.css`,
`settings/design-system.css`, `topbar/bar.css` и падает на любом hex вне множества
`{0B0B0B,161616,434343,808080,C8C8C8,FFFFFF,5E1119,8C1A25,B8BDC2,6E7276,D9DEE2,8E9498,
F2F5F7,9AA0A6,1D1D1D,676767,000000,050505}`. rgba(...) с этими же цифрами (прозрачность)
не запрещён: тест ищет только hex-литералы.

## Шрифты

Две гарнитуры, курсив нигде, цифры tabular-nums:

- **Roboto Flex** (вариативный, `ui/fonts/RobotoFlex-Variable.ttf`): `font-weight: 100 1000`,
  `font-stretch: 25% 151%`. Роли display/heading/subhead/body.
- **JetBrains Mono** (`ui/fonts/JetBrainsMono-Regular.ttf` 400, `ui/fonts/JetBrainsMono-Medium.ttf`
  500): роли label/number/greek/signature.

Лицензия обеих: SIL OFL 1.1, тексты рядом с файлами (`ui/fonts/OFL-*.txt`), см. `THIRD-PARTY.md`.

## Роли текста (утилитарные классы, `ui/tokens.css`)

| Класс | Гарнитура | wdth/wght или вес | Регистр | Трекинг |
|---|---|---|---|---|
| `.t-display` | Roboto Flex | 148 / 300 | upper | -0.015em |
| `.t-heading` | Roboto Flex | 145 / 350 | upper | -0.01em |
| `.t-subhead` | Roboto Flex | 130 / 400 | upper | 0 |
| `.t-body` | Roboto Flex | 100 / 400 | обычный | 0 |
| `.t-label` | JetBrains Mono | 400, 10px (`.is-lg` → 13px) | upper | 0.14em |
| `.t-number` | JetBrains Mono | 500, tabular-nums |: | 0.06em |
| `.t-greek` | JetBrains Mono | 400 | lower | 0.22em, цвет Steel |
| `.t-signature` | JetBrains Mono | 400 | upper | 0.14em, цвет Steel |

## Ряды токенов

- Отступы/радиусы (Фибоначчи): `3 5 8 13 21 34 55 89 144 233`: `--sp-3` … `--sp-233`.
- Кегли (шаг √φ ≈ 1.272): `10 13 16 20 26 33 42 53 68 86 110 140 178`: `--fs-10` … `--fs-178`.

Все размеры и отступы компонентов: только из этих рядов, никаких произвольных пикселей.

## Компоненты (`ui/components.css` + `ui/components.js`)

`ui/components.js`: UMD-модуль: обычный `<script>` ставит `window.NNAUI`, в CommonJS доступен
через `require`/`module.exports`. Чистые функции без DOM (позиционирование, клавиатурная
навигация) вынесены в `ui/logic.js`: покрыты юнит-тестами (`ui/tests/components.test.mjs`) без
браузерного окружения.

Порядок загрузки в HTML: `tokens.css`, `components.css`, `logic.js`, `components.js`, затем
код страницы (`app.js` и т.п.).

| Вызов | Что делает |
|---|---|
| `NNAUI.select(el, {options, value, onChange, searchable})` | Заменяет `<select>` либо монтируется в контейнер. Кнопка-триггер + выпадающий список, `role=listbox`, клавиатура (стрелки, Home/End, Enter, Esc, ввод букв: переход по первой букве), закрытие по клику мимо. |
| `NNAUI.toggle(el, {checked, onChange, label})` | Переключатель-капсула (34×21), `role=switch`, `aria-checked`, Space/Enter. |
| `NNAUI.slider(el, {min,max,step,value,onInput,onChange,format})` | Трек 3px + ручка 13px, драг мышью, клавиатура (стрелки, PageUp/Down, Home/End), подпись значения моно tabular-nums. |
| `NNAUI.segmented(el, {items, value, onChange})` | Сегментированный контрол, `role=radiogroup`, стрелки влево/вправо. |
| `NNAUI.popover(anchorEl, {content, placement, onClose})` | Слой поверх, позиция от якоря с учётом краёв окна (`placePopover` в `ui/logic.js`), закрытие по Esc и клику мимо. Возвращает `{close, el}`. |
| `NNAUI.tooltip(el, text)` | Показ через 400мс при hover/focus, моно 10px. |
| `NNAUI.dialog({title, body, actions})` | Модальный диалог, фокус-ловушка (Tab по кругу), Esc. |
| `NNAUI.menu(anchorEl, {items})` | Контекстное меню, клавиатура (стрелки, Enter, Esc), поддержка `separator`/`disabled`/`hint`. |

Кнопки и поля: классы без вызова JS: `.ui-btn`, `.ui-btn.primary` (White на Base, текст Base),
`.ui-btn.ghost`, `.ui-btn.danger`, `.ui-icon-btn`, `.ui-field` (label моно + input), `.ui-card`
(Surface, скругление 13, граница Slate 0.5).

Скроллбар: глобальный `::-webkit-scrollbar` (ширина 8, ползунок Slate, hover Steel) плюс класс
`.ui-scroll`: тонкий overlay-скролл, прозрачный до наведения.

Переходы: только по конкретным свойствам, 120–180ms, без `transition: all`. Теней у карточек
нет; лёгкая тень допустима у поповеров/меню (`0 8px 21px rgba(0,0,0,.45)`): это приём macOS.

## Демо

`ui/demo.html`: все роли текста и компоненты на одной тёмной странице (скриншот для проверки).
Открыть через локальный статический сервер, например:
`python -m http.server 8765 --directory <repo>` → `http://127.0.0.1:8765/ui/demo.html`.

## Проверка

```
node --test ui/tests/*.test.mjs
```

`palette.test.mjs`: цвета из палитры; `tokens.test.mjs`: состав `ui/tokens.css` и наличие
файлов шрифтов/OFL; `components.test.mjs`: чистые функции из `ui/logic.js`
(`placePopover`, `sliderStep`, `nextIndex`, `typeahead`).
