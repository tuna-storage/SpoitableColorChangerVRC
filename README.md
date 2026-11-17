# Spoitable Color Changer

アバターのテクスチャ(髪など)を、NDMF のビルド時にグラデーションで塗り替える Unity エディタ拡張です。元のテクスチャ・マテリアル・プレハブは書き換えません。

- レイヤーごとに色(グラデーション)を決め、塗りマスク・画像マスク・グラデーションマスクで色を乗せる範囲を決めます。
- 色の経路を使うと、点を結んだ線に沿って色を並べられます(根元から毛先へ、など)。
- NDMF のプレビューで、ビルドしなくても結果を確認できます。

## 導入(VCC)

1. VCC の Settings → Packages → Add Repository に、次の URL を追加します。

   ```
   https://tuna-storage.github.io/SpoitableColorChangerVRC/index.json
   ```

   [リストのページ](https://tuna-storage.github.io/SpoitableColorChangerVRC/) の「Add to VCC」からも追加できます。
2. アバターのプロジェクトの Manage Project で、Spoitable Color Changer を追加します。NDMF も一緒に入ります。

前に `Assets/faketuna/SpoitableColorChanger` に入れていた場合、パッケージを入れるとそのフォルダは自動で取り除かれます(シーンの設定はそのまま使えます)。

## 必要なもの

- Unity 2022.3
- VRChat SDK - Avatars 3.7.0 以上
- NDMF 1.13.0 以上(2.0 未満)

## 使い方

1. アバターの中のオブジェクトに `Spoitable Color Changer` コンポーネントを付け、対象の Renderer かマテリアルと、テクスチャのプロパティを選びます。
2. プリセットを作り、インスペクタの「エディタを開く」(または Tools → faketuna → Spoitable Color Changer)でエディタを開きます。
3. 右のパネルでレイヤーの色を決め、キャンバスで色を乗せる範囲を塗ります。操作の一覧はエディタ右上の「?」にあります。
4. アバターをビルド(アップロード)すると、塗り替えたテクスチャが使われます。

## ライセンス

[MIT License](LICENSE)
