# VRCLightmapMixer

VRCLightmapMixer は、VRChat ワールド内で複数の Bakery ベイク済みライトマップの切り替え・混合を実現するライブラリです。

通常のライトマップは実行時に差し替えたりブレンドしたりできません。このパッケージでは、Lightmap A / Lightmap B の 2 種類の Bakery ベイク結果を用意し、実行時に専用シェーダへマテリアルを差し替えることで、ライトの明るさをリアルタイムに調整できるようにします。

## 必要なもの

- Unity 2022.3 系の VRChat ワールドプロジェクト
- VRChat SDK Worlds
- UdonSharp
- Bakery

Bakery はこのパッケージには含まれていません。先にプロジェクトへ導入し、通常の Bakery ライトベイクができる状態にしておいてください。

## サンプルの試し方

1. プロジェクトに Bakery を入れます。
2. VRCLightmapMixer のパッケージをインポートします。
3. `Jumius/VRCLightmapMixer/Sample/SampleScene` を開きます。
4. シーン内の `LightmapMixer` オブジェクトを選択します。
5. Inspector の `4. レンダリング` から `ライトベイクを実行` を押します。
6. ベイク中にシーン保存のダイアログが出たら、保存を押します。
7. `ライトベイクが完了しました。` というダイアログが出たらベイク完了です。
8. シーンを実行します。
9. シーン内のスライダーを動かすと、Lightmap A / Lightmap B に対応するライトの明るさを変えられます。

`LightmapMixer` の Inspector にある `3. 実行時の調整` からもライトの明るさを変更できます。ただし、サンプルシーンでは `LightControlUI` のスライダーも同じ値を操作します。Inspector で直接調整したい場合は、`LightControlUI` オブジェクトを非表示にしてください。

## 自分のプロジェクトで使う方法

### 1. Bakery の準備

プロジェクトに Bakery を入れ、通常の Bakery ライトベイクができるようにセットアップします。

先に Bakery 単体でベイクできる状態まで作ってから、VRCLightmapMixer の設定に進むのがおすすめです。

### 2. LightmapMixer を配置する

適当な空の GameObject を作成し、`LightmapMixer` コンポーネントをアタッチします。

`置き換え用シェーダ` には、次のシェーダをセットします。

```text
Shader/Standard_MultiLightmap
```

このシェーダは Unity built-in Standard shader をベースにした改造版です。実行時に Lightmap A / Lightmap B の追加ライトマップを参照します。

### 3. 対象レンダラーを設定する

`対象レンダラーを収集` ボタンを押します。

`対象レンダラー一覧` に、ライトマップ適用対象になっているメッシュの Renderer が並びます。基本的には自動収集された一覧をそのまま使います。

一部の Renderer を除外したい場合は、`ベイク時に自動で収集` のチェックを外してから、`対象レンダラー一覧` を手動編集してください。チェックが入っている場合、ベイク実行時に対象レンダラー一覧が再収集されます。

### 4. Lightmap A / B で切り替えるオブジェクトを設定する

`Lightmap Aで有効化するオブジェクト` と `Lightmap Bで有効化するオブジェクト` に、それぞれのベイク時だけ有効にしたいライトオブジェクトを入れます。

例えば、昼用ライトを Lightmap A、夜用ライトを Lightmap B として焼きたい場合は、昼用ライトを A 側、夜用ライトを B 側に入れます。

`ライトベイクを実行` を押すと、Lightmap A 用の状態で 1 回、Lightmap B 用の状態で 1 回、Bakery ベイクが自動で実行されます。

### 5. リフレクションプローブを設定する

リフレクションプローブも A / B それぞれに設定できます。

`Lightmap Aでベイクするリフレクションプローブ` と `Lightmap Bでベイクするリフレクションプローブ` に、それぞれの状態で焼きたい Reflection Probe の GameObject を入れます。

A 用の Reflection Probe を作ったあと、それを複製して B 用にするのが簡単です。その際、A 用と B 用が完全に同じ位置だと扱いづらいことがあるため、片方を少しだけ、例えば `0.01` 程度ずらしておくと管理しやすくなります。

Renderer 側の `Probe Mode` は `Blend Probes` にしておきます。通常はデフォルトでこの設定になっています。

### 6. 発光マテリアルをライトとして使う

発光マテリアルを光源として使う場合、A / B でオブジェクトを On / Off する代わりに、Emission の強さを切り替えることができます。

`Lightmap Aで光らせるマテリアル` と `Lightmap Bで光らせるマテリアル` に、対応する発光マテリアルをセットしてください。

ベイク中は A / B に応じて `_EmissionColor` が切り替わります。実行時には、起動時に複製されたマテリアルに対して Emission が調整されます。

### 7. 特殊なシェーダパラメータを切り替える

特殊なシェーダを使って光る物体がある場合、A / B で別々のシェーダパラメータ値をセットできます。

`Lightmapごとにシェーダパラメータを変えるマテリアル` に、次の組み合わせを追加します。

- 対象マテリアル
- シェーダパラメータ名
- Lightmap A のときの値
- Lightmap B のときの値

例えば、空のシェーダが昼と夕方を float パラメータで切り替える作りになっている場合、そのパラメータを Lightmap A / B のベイク前に切り替えられます。

### 8. ライトプローブを設定する

ライトプローブを使う場合は、通常通りシーンに Light Probe Group を配置しておきます。

ライトプローブを使わない場合は、Inspector の `5. Advanced Settings` にある `Render Light Probes After Lightmaps` のチェックを外してください。

### 9. ベイクを実行する

セットアップが完了したら、`LightmapMixer` の Inspector にある `4. レンダリング` から `ライトベイクを実行` を押します。

通常の Bakery のベイクボタンは使わないでください。`LightmapMixer` の `ライトベイクを実行` ボタンを押すことで、Lightmap A / B 用の 2 回のベイク、ライトプローブ、リフレクションプローブ、ライトマップのコピーと割り当てがまとめて実行されます。

ベイクが完了すると、`Lightmap A Textures` と `Lightmap B Textures` に生成されたライトマップが自動でセットされます。

## 実行時の調整

`LightmapMixer` の `3. 実行時の調整` では、実行中のライトマップ混合値を変更できます。

- `Runtime Mix Use`: 追加ライトマップ混合を使う量
- `Runtime Mix A`: Lightmap A の明るさ
- `Runtime Mix B`: Lightmap B の明るさ
- `Runtime Mix Step`: ボタン操作などで増減させる場合のステップ幅

スクリプトから操作したい場合は、`LightmapMixerSliderController` を参照してください。UI Slider の `OnValueChanged` から `LightmapMixer` を呼び出し、同期された値を各プレイヤーへ反映するサンプルになっています。

## 注意点

- `LightmapMixer` のベイク機能は Bakery に依存します。
- 実行時のマテリアル差し替えには `Shader/Standard_MultiLightmap` が必要です。
- 対象 Renderer のライトマップインデックスが変わった場合は、`対象レンダラーを収集` を押し直してください。
- Emission を実行時に切り替えるため、専用シェーダでは `_EMISSION` バリアントを常に含めるようにしています。
- `Shader` フォルダ内の Standard_MultiLightmap 関連ファイルは、Unity built-in shader source の MIT ライセンス版を改造したものです。
