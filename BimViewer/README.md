# Sightline BIM Viewer

An interactive, browser-based BIM model viewer built with [three.js](https://threejs.org) and
[web-ifc](https://github.com/ThatOpen/engine_web-ifc). It is a single file, `index.html`, with no build step.

## Run it

```bash
cd BimViewer
python3 -m http.server 8000   # or: npx serve .
```

Then open http://localhost:8000. The page opens on a built-in sample building (Riverside Office Block).
Use **Open IFC** to replace the model with your own `.ifc` files (IFC2X3 / IFC4), or **Add IFC**
(or drag files onto the viewport) to merge more files into the current model.
Libraries are loaded from cdn.jsdelivr.net, so the browser needs internet access.

## Features

- Orbit, pan, zoom; Iso / Plan / South / East / Fit camera presets
- Click to select, double-click to zoom to an element; hover highlight
- Model tree grouped by storey and IFC class, with search and per-element / per-storey visibility
- IFC class filter chips (e.g. hide all windows)
- Properties panel: GlobalId, storey, material, property sets and quantity sets
- Hide, isolate element, isolate storey, show all
- **Merge files**: load architecture, structure, MEP… as one federated model. Every file after the
  first gets the same origin shift, so they stay aligned. Each file appears in the tree, where you can
  hide or remove it, and **Colour by file** tints each discipline.
- **Sectioning in any direction**:
  - `+ X` / `+ Y` / `+ Z`: planes on the main axes
  - **On surface**: click any face to put a plane on it
  - **From view**: cut square to your line of sight
  - **Box**: a six-sided section box (or **Section box** around the selected element)
  - Drag the arrows in 3D, or use each cut's slider; rotate a plane to any angle with Turn and Tilt;
    flip it to cut the other side; stack as many cuts as you like
  - Cut faces are filled solid, like poché on a drawing
- X-ray mode and exploded storeys
- Two-point measuring tool (distance and height difference in mm)
- Room tags and a room schedule; room types use the same rule as `ConsoleApp1/Program.cs`
  (< 10 m² Store, < 30 m² Office, otherwise Meeting room)
- Light and dark themes, works on phones

## Keyboard

| Key | Action |
| --- | --- |
| `F` | Zoom to the selection |
| `H` | Hide the selection |
| `M` | Toggle measuring |
| `X` | Toggle X-ray |
| `Esc` | Cancel a measurement / clear the selection |
