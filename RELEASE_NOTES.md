# VRCLightmapMixer Release Notes

## 0.1 - Test Release

VRCLightmapMixer 0.1 は、VRChat ワールドで複数の Bakery ベイク済みライトマップを切り替え・混合する仕組みを試すためのテスト版です。

このバージョンは実運用前の検証を目的としています。プロジェクトへ導入する場合は、事前にバックアップを取り、サンプルシーンや小規模な検証シーンで動作を確認してから使用してください。

### 主な機能

- Lightmap A / Lightmap B の 2 種類の Bakery ベイク結果を生成できます。
- `LightmapMixer` の Inspector から A / B 用のライトベイクをまとめて実行できます。
- ベイク結果のライトマップを A / B 用フォルダへコピーし、`Lightmap A Textures` / `Lightmap B Textures` に自動設定します。
- 実行時に対象 Renderer のマテリアルを複製し、`Shader/Standard_MultiLightmap` へ差し替えます。
- グローバルシェーダ変数で Lightmap A / B の明るさをリアルタイムに調整できます。
- Reflection Probe の A / B ベイクと、実行時 intensity 調整に対応しています。
- Lightmap A / B ごとに有効化するオブジェクトを指定できます。
- Lightmap A / B ごとに発光させるマテリアルを指定し、Emission を切り替えられます。
- Lightmap A / B ごとに任意のマテリアルシェーダパラメータを切り替えられます。
- UI Slider から Mix A / Mix B を操作する UdonSharp サンプルを含みます。
- サンプルシーン `Sample/SampleScene` を含みます。

### 付属シェーダ

- `Shader/Standard_MultiLightmap`
- `Shader/UnityStandardCore_MultiLightmap.cginc`
- `Shader/UnityStandardCoreForward_MultiLightmap.cginc`

これらは Unity built-in shader source の MIT ライセンス版をベースにした改造品です。
