---
name: Widget request
about: Suggest a new built-in widget, or ask a question about writing your own
title: ""
labels: widget
---

**What should the widget show or do?**
A short description of the widget's purpose.

**Data source**
Where does the data come from? An existing local API route (see `docs/ARCHITECTURE.md`), a new
one you think the host would need to add, or an external service the widget itself would call?

**Size and placement**
Roughly how big is this widget (in grid cells), and is it meant for a specific monitor layout
(e.g. a narrow vertical monitor vs. a wide one)?

**Are you planning to build this yourself?**
See `docs/WIDGET-SDK.md` for the manifest format. If yes, feel free to open a draft PR instead of
(or alongside) this issue.
