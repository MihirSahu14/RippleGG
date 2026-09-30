# Ripple

A short narrative game about how small decisions add up — two teenagers, Avery and Skyler, in a small town by the sea and the train tracks. Built end to end with [GameGold](https://github.com/MihirSahu14/GameGold): story, art, player settings and the web build all came through GameGold's UI and its Unity bridge.

- Engine: Unity 6.5 (6000.5.1f1), 2D
- Scene: `Assets/Scenes/Story.unity`
- Story data: `Assets/Resources/GameGold/dialogue.json` (played by GameGold's `DialoguePlayer`)

## Opening the project
`Packages/manifest.json` points the GameGold bridge package at a local GameGold checkout:

```json
"com.gamegold.mcp": "file:C:/Users/mihir/Desktop/Projects/GameGold/unity-mcp"
```

On another machine, replace that line with the public package before opening in Unity:

```json
"com.gamegold.mcp": "https://github.com/MihirSahu14/GameGold.git?path=/unity-mcp"
```

## Playing
Open `Story.unity` and press Play, or build for web from GameGold's Unity page ("Build for web").
