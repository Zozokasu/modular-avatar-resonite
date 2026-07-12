# Implementation Plan: lilToon 影設定 → ShadowRamp / 輪郭線設定 → XiexeToon Outline 変換

> 方針元: `Assets/plans/plan.md`(MAResoDevelopment プロジェクトルート)
> 作成: 2026-07-13 / マルチモデル分析(Codex + Claude 統合。Gemini は課金クレジット枯渇のため未使用)

## Task Type
- [x] Backend (→ Codex)
- [ ] Frontend
- [ ] Fullstack

UI 作業なし。Unity Editor 側のシリアライズ処理 + Puppeteer(FrooxEngine バックエンド)側のマテリアル構築処理の変更。

---

## 背景と現状のギャップ(コード調査で確認済みの事実)

- **変換パイプライン**: Unity 側 `AvatarSerializer.TranslateMaterial`(`Editor/SerializeAvatar/AvatarSerializer.cs:566`) → protobuf(`reso-input.pb`) → バックエンド `RootConverter.CreateMaterial` → `CreateXSToonMaterial`(`Resonite~/ResoniteHook/Puppeteer/conversion/Material.cs:177`)。
- **lilToon プロパティは読み取り済みだが未出力**: `LiltoonBaker.cs` が `_UseShadow` / `_ShadowColor` / `_ShadowBorder` / `_ShadowBlur` / `_Shadow2nd*` / `_Shadow3rd*` / `_ShadowBorderColor` / `_ShadowStrengthMask`(:171-208)、`_OutlineColor` / `_OutlineWidth` / `_OutlineWidthMask`(:409-428)を `lilMaterialProperty` として既にバインドしているが、proto に一切書き出していない。**ここが今回埋めるギャップ。**
- **proto スキーマ**: `Resonite~/ResoniteHook/ResoPuppetSchema/proto/asset.proto` の `message Material` はフィールド番号 1-19 を使用中。影・輪郭線フィールドは存在しない。
- **フォールバック ShadowRamp**: `CreateXSToonExemplar`(`Material.cs:325`)で `resdb:///213a4363...webp` をハードコード(`// TODO: don't hardcode this` コメントあり)。これがオレンジがかったデフォルトランプの正体。
- **共有 exemplar バインディング**: `BindExemplarValues`(`Material.cs:256`)が `ShadowRamp` / `Outline` / `OutlineWidth` / `OutlineColor` / `OutlineAlbedoTint` / `ShadowSharpness` 等を DynamicField(変数名 `modular_avatar/xs_toon_template.<field>`)で単一の共有 exemplar にバインドしている。`ShadowRampMask` / `OutlineMask` は TODO 未実装。
- **生成テクスチャ注入の前例**: `BakeMetallicMap`(`Editor/ShaderSupport/LiltoonShaderSupport.cs:106`)が in-memory `Texture2D` を `textureImporter(newTex, null, out id, out _)` で proto に注入済み(EncodeToPNG インラインブロブ経路)。同じ方式が使える。
- **lilToon 側の再利用可能 API**(lilToon 2.3.4, `Packages/jp.lilxyzw.liltoon`):
  - `lilToon.lilToon2Ramp.Convert(Material origin, int width = 128)` — **public static**。マテリアルを複製し `Hidden/ltsother_bakeramp` シェーダーで GPU blit → 128×16 RGBA32 sRGB `Texture2D` を返す(x=0 が陰側、x=1 が受光側=白。全 16 行同一)。
  - `lilToon.lilShaderUtils.IsOutlineShaderName(string)` / `IsMultiShaderName(string)` — 輪郭線バリアント判定。
  - ランプの向きは XiexeToon ShadowRamp(右=受光、左=陰)と**一致しており反転不要**。
  - 制限: bake シェーダーは `_ShadowColorTex` / `_ShadowBorderMask` / `_ShadowBlurMask` / `_ShadowAOShift` 等を無視する(スカラー・カラー設定のみ反映)。**許容する制限**とする。

---

## Technical Solution(統合方針)

1. **proto に per-material の影・輪郭線フィールドを追加**し、「フィールド未設定 = 従来どおり exemplar 共有バインドにフォールバック」「設定あり = per-material 値を直接セットしバインドをスキップ」という **presence ベースのセマンティクス**で後方互換を保つ(Codex 提案を採用)。
2. Unity 側 `LiltoonShaderSupport` で `lilToon2Ramp.Convert` を呼び、**縦方向ホワイトグラデーションを後処理**した ShadowRamp を生成して proto に注入。`_ShadowStrengthMask` を `ShadowRampMask` として、`_OutlineWidthMask` を `OutlineMask` として転送。
3. バックエンド側 `CreateXSToonMaterial` で新フィールドを per-material に適用し、`BindExemplarValues` を「proto で届いていないフィールドのみバインド」に変更。

### ShadowRamp 縦グラデーションの仕様(plan.md §「ShadowRampにグラデーションを入れる」)

XiexeToon は `ShadowRampMask.R` の値(リニア)で ramp を縦にサンプリングする: R=1.0 → 最上部、0.5 → 中部、0.0 → 最下部。lilToon の `_ShadowStrengthMask`(白=影フル適用)と互換にするため:

- 最上部の行 = lilToon 生成 ramp そのまま(白グラデのアルファ 0)
- 最下部の行 = 完全な白 RGBA(1,1,1,1)(影なし)
- 中間 = 白のアルファがリニアに変化するアルファブレンド

```
out(x, y) = lerp(rampColor(x), white, 1 - y/(H-1))   // y=0 が最下部(Unity SetPixel 座標)
out.a = 1
```

- マスクありの場合の出力解像度: **128(幅)×128(高さ)**(16px のままだと縦方向のグラデーションがバンディングするため)。
- `_ShadowStrengthMask` が無い(null)場合はグラデーション不要 → 128×16 のままで可(実装簡略化のため常に 128×128 に統一しても良い。実装時にどちらかへ寄せる)。
- `_ShadowStrength` スカラーや 2nd/3rd 影は `lilToon2Ramp` が ramp に焼き込み済みなので追加処理不要。

### Outline 変換仕様(plan.md §「OutlineColorとOutlineMask」)

- 判定: `IsMultiShaderName(shader.name)` なら `_UseOutline > 0.5`、それ以外は `IsOutlineShaderName(shader.name)`(シェーダー名の最後の `/` 以降に "Outline" を含むか)。
- ON → XiexeToon `Outline` enum = **Lit** / OFF → **None**。
- `_OutlineColor` → `OutlineColor`(ColorProfile = **sRGB**。HDR 値は float のまま保持)。
- `_OutlineWidthMask` → `OutlineMask`(R チャンネルが幅乗数、XiexeToon と同セマンティクス)。
- `_OutlineWidth` → `OutlineWidth`: lilToon はシェーダー内で `_OutlineWidth * 0.01`(オブジェクト空間メートル)。**暫定で `_OutlineWidth * 0.01` を proto に格納**(「proto の値 = Resonite にそのまま入れられる値」と定義)。XiexeToonMaterial 側の単位は未確認のため、実機で目視検証し、必要ならスケール係数を修正する(Risk 参照)。

---

## Implementation Steps

### Step 1: proto スキーマ拡張
**成果物**: `ResoPuppetSchema/proto/asset.proto` の `Material` 拡張 + C# 再生成

```protobuf
enum ToonOutlineMode {
  TOON_OUTLINE_NONE = 0;
  TOON_OUTLINE_LIT = 1;
}

message Material {
  // ...既存 1-19...
  optional AssetID shadow_ramp = 20;       // 生成した per-material ランプ
  optional AssetID shadow_ramp_mask = 21;  // _ShadowStrengthMask 由来
  optional ToonOutlineMode outline = 22;
  optional Color outline_color = 23;
  optional float outline_width = 24;       // Resonite-ready 値(= lilToon _OutlineWidth * 0.01)
  optional AssetID outline_mask = 25;      // _OutlineWidthMask 由来
}
```

**presence セマンティクス**(重要・Codex 提案採用):
- フィールド**未設定** → バックエンドはそのフィールドを従来どおり exemplar にバインド(非 lilToon マテリアルの挙動不変)。
- `AssetID { id: 0 }` を**明示的 null**として扱う → per-material に null をセットし、exemplar バインドもスキップ(lilToon マテリアルが exemplar のマスクを継承してしまう事故を防ぐ)。
- C# 再生成は既存の生成フロー(ResoPuppetSchema の csproj / Grpc.Tools 等)に従う。ビルドして生成物(`HasOutline` 等の presence API)を確認。

### Step 2: Unity 側 — ShadowRamp 生成・後処理・proto 出力
**成果物**: `Editor/ShaderSupport/LiltoonShaderSupport.cs`(+必要なら `LiltoonBaker.cs`)に `TranslateLiltoonShadow` 追加。`TryTranslateMaterial`(:22)成功後に呼び出し。

```csharp
// #if MA_LILTOON_PRESENT 内
void TranslateLiltoonShadow(Material material, p.Material protoMat)
{
    bool useShadow = material.GetFloatSafe("_UseShadow") > 0.5f;
    Texture shadowMask = shadowStrengthMask.textureValue; // 既存 lilMaterialProperty

    Texture2D ramp;
    if (useShadow)
    {
        var baseRamp = lilToon.lilToon2Ramp.Convert(material, 128); // 128x16 sRGB
        ramp = (shadowMask != null)
            ? ApplyVerticalWhiteGradient(baseRamp, 128, 128)  // 縦グラデ合成
            : baseRamp;                                        // そのまま
    }
    else
    {
        ramp = MakeSolidTexture(128, 16, Color.white); // 影OFF = 全白ランプ
    }
    ramp.wrapMode = TextureWrapMode.Clamp;
    _tempObjects.Add(ramp); // 既存の一時オブジェクト破棄機構に載せる

    if (textureImporter(ramp, null, out var rampId, out _))
        protoMat.ShadowRamp = rampId;

    if (useShadow && shadowMask != null
        && textureImporter(shadowMask, shadowMask, out var maskId, out _))
        protoMat.ShadowRampMask = maskId;
    else
        protoMat.ShadowRampMask = new p.AssetID { Id = 0 }; // 明示的 null
}

Texture2D ApplyVerticalWhiteGradient(Texture2D baseRamp, int w, int h)
{
    var dst = new Texture2D(w, h, TextureFormat.RGBA32, false, false); // sRGB
    var basePixels = baseRamp.GetPixels32(); // 元は16行同一なので行0を使う
    for (int y = 0; y < h; y++)
    {
        float whiteAlpha = 1f - y / (float)(h - 1); // y=0(最下部)=白100%
        for (int x = 0; x < w; x++)
        {
            Color c = basePixels[x]; // 行0のx列
            var o = Color.Lerp(c, Color.white, whiteAlpha);
            o.a = 1f;
            dst.SetPixel(x, y, o);
        }
    }
    dst.Apply();
    return dst;
}
```

色空間ノート: `lilToon2Ramp.Convert` は sRGB(linear=false)の RGBA32 を返す。後処理も sRGB バイト空間のまま行い、既存 `TranslateTexture2D` の `EncodeToPNG` 経路にそのまま乗せる。生成テクスチャはアセット非登録なので `IsNormalMap` は false のまま。Unity の `Color` float 変換で 1 バイト単位のズレが出る場合は `Color32` 演算に切り替える。

### Step 3: Unity 側 — Outline 変換
**成果物**: 同ファイルに `TranslateLiltoonOutline` 追加。

```csharp
void TranslateLiltoonOutline(Material material, p.Material protoMat)
{
    string shaderName = material.shader.name;
    bool outlineOn = lilToon.lilShaderUtils.IsMultiShaderName(shaderName)
        ? material.GetFloatSafe("_UseOutline") > 0.5f
        : lilToon.lilShaderUtils.IsOutlineShaderName(shaderName);

    protoMat.Outline = outlineOn ? p.ToonOutlineMode.ToonOutlineLit
                                 : p.ToonOutlineMode.ToonOutlineNone;
    if (!outlineOn)
    {
        protoMat.OutlineMask = new p.AssetID { Id = 0 };
        return;
    }

    var c = outlineColor.colorValue.ToRPC();   // 既存変換ヘルパ
    c.Profile = p.ColorProfile.SRgb;
    protoMat.OutlineColor = c;
    protoMat.OutlineWidth = outlineWidth.floatValue * 0.01f; // → object-space meters

    var mask = outlineWidthMask.textureValue;
    if (mask != null && textureImporter(mask, mask, out var maskId, out _))
        protoMat.OutlineMask = maskId;
    else
        protoMat.OutlineMask = new p.AssetID { Id = 0 };
}
```

### Step 4: バックエンド — per-material 適用 + exemplar バインドのスキップ
**成果物**: `Puppeteer/conversion/Material.cs` の変更。

1. `CreateXSToonMaterial` 内(テクスチャ参照は既存の `PHASE_RESOLVE_REFERENCES` 遅延ブロックに追加):

```csharp
Defer(PHASE_RESOLVE_REFERENCES, ..., () => {
    // 既存のテクスチャ設定に追記
    if (src.ShadowRamp != null)
        mat.ShadowRamp.Value = AssetRefID<f.IAssetProvider<f.Texture2D>>(src.ShadowRamp);
        // id==0 のときは RefID.Null 相当(明示的 null)
    if (src.ShadowRampMask != null)
        mat.ShadowRampMask.Value = ...同様...;
    if (src.OutlineMask != null)
        mat.OutlineMask.Value = ...同様...;
});

if (src.HasOutline)           mat.Outline.Value      = /* Lit or None(Froox 側 enum にマップ)*/;
if (src.OutlineColor != null) mat.OutlineColor.Value = src.OutlineColor.ColorX(); // 既存変換
if (src.HasOutlineWidth)      mat.OutlineWidth.Value = src.OutlineWidth;
```

2. `BindExemplarValues(mat, _xsExemplar)` に `src` を渡し、**presence ベースで**バインドをスキップ:

```csharp
if (!src.HasOutline)            BindField(mat.Outline);
if (!src.HasOutlineWidth)       BindField(mat.OutlineWidth);
if (src.OutlineColor == null)   BindField(mat.OutlineColor);
if (src.OutlineMask == null)    BindField(mat.OutlineMask);     // 新規(現状TODO)
if (src.ShadowRamp == null)     BindField(mat.ShadowRamp);
if (src.ShadowRampMask == null) BindField(mat.ShadowRampMask);  // 新規(現状TODO)
```

`CreateXSToonExemplar` のハードコード ramp(:325)は**フォールバックとして残す**(非 lilToon マテリアル用)。

3. `OutlineAlbedoTint` は今回スコープ外(exemplar バインド継続)。

### Step 5: ビルド・検証
**成果物**: 動作確認済みのエクスポート。

1. proto 再生成 → ResoniteHook ソリューションと Unity 両方のコンパイル確認。
2. テストマテリアル行列でエクスポート(下記 Test Strategy)。
3. Resonite 内で目視確認: 影色、境界、`_ShadowStrengthMask` の効き、輪郭線の有無・色・太さ・マスク。
4. `OutlineWidth` スケール係数の実機キャリブレーション(0.01 係数が合わない場合はここで補正)。

---

## Key Files

| File | Operation | Description |
|------|-----------|-------------|
| `Resonite~/ResoniteHook/ResoPuppetSchema/proto/asset.proto` | Modify | `Material` にフィールド 20-25 + `ToonOutlineMode` enum 追加 |
| `Editor/ShaderSupport/LiltoonShaderSupport.cs` | Modify | `TranslateLiltoonShadow` / `TranslateLiltoonOutline` 追加、`TryTranslateMaterial`(:22)から呼び出し |
| `Editor/ShaderSupport/LiltoonBaker.cs` | Modify (小) | 必要ならグラデ合成ヘルパ配置(既存 `RunBake` :1022 と同居) |
| `Resonite~/ResoniteHook/Puppeteer/conversion/Material.cs` | Modify | `CreateXSToonMaterial`(:177)per-material 適用、`BindExemplarValues`(:256)presence ベーススキップ、`ShadowRampMask`/`OutlineMask` の TODO 解消 |

## Edge Cases

| ケース | 挙動 |
|--------|------|
| `_UseShadow` ≤ 0.5 | 全白 128×16 ランプを per-material 生成、`ShadowRampMask` は明示的 null |
| 2nd/3rd 影の alpha = 0 | `lilToon2Ramp` が自然に無効化(追加処理不要) |
| lilToonMulti シェーダー | 輪郭線判定は `_UseOutline` プロパティで行う |
| `_ShadowStrengthMask` / `_OutlineWidthMask` 未設定 | 明示的 null(`AssetID{id:0}`)を送り、exemplar のマスクを継承させない |
| HDR `_OutlineColor`(>1) | proto Color は float なので保持。Profile=sRGB |
| `Hidden/ltsother_bakeramp` シェーダー欠落 | 警告ログ + 従来 exemplar ランプへの degraded フォールバック |
| `MA_LILTOON_PRESENT` 未定義 | 従来どおり GenericShaderTranslator 経路(挙動不変) |
| FakeShadow カテゴリ | 既存どおり不可視マテリアル(本機能の対象外) |

## Risks and Mitigation

| Risk | Mitigation |
|------|------------|
| XiexeToon `OutlineWidth` の単位が想定(object-space m)と異なる | proto は「Resonite-ready 値」と定義し、実機で幅 0.02/0.05/0.1 を目視検証。ズレたら Unity 側の係数のみ修正 |
| FrooxEngine の `ShadowRampMask`/`OutlineMask`/`Outline` フィールドの正確な型・enum メンバー名が未確認 | コンパイル時に確認(TODO コメントで存在自体は示唆済み)。enum が無く bool の場合は proto enum → bool にマップ |
| EncodeToPNG / ガンマ処理でランプのバイト値が微妙に変わる | ランプ pixel テスト(lilToon の `ConvertAndSave` 出力 PNG とバイト比較) |
| RampMask null 時の XiexeToon サンプリング挙動が未文書化 | マスク無しマテリアルで実機確認。問題があれば常に全白マスクを明示送信 |
| exemplar スキップを値ベースで書くと `None`/null が上書きされる | **presence ベース**(`HasXxx` / null 判定)で実装し、バックエンドテストで DynamicField 非付与を検証 |
| proto3 `optional` スカラーの C# 生成互換 | 既存生成フローで `HasOutline` 等が生えることをビルドで確認 |

## Test Strategy

1. **Editor 単体テスト**: lilToon マテリアルを組み立て、proto の `ShadowRamp`/`ShadowRampMask`/`Outline`/`OutlineColor`/`OutlineWidth`/`OutlineMask` の presence と値をアサート。
2. **ランプ pixel テスト**: 最上行 = `lilToon2Ramp` 出力と一致 / 最下行 = 白 / 中間行 = リニア補間値。マスク無しケースでは縦方向不変。
3. **バックエンドテスト**: per-material フィールド入り proto → 該当 XiexeToon フィールドに DynamicField が付かないこと。フィールド無し proto → 従来どおり exemplar バインドされること(回帰)。
4. **統合(目視)マトリクス**: 影OFF / 基本影(1st のみ) / 2nd+3rd 影 / `_ShadowStrengthMask` あり / 輪郭線 OFF / 輪郭線 ON(色・太さ・マスク) / lilToonMulti の `_UseOutline` ON・OFF / 非 lilToon マテリアル(回帰)。

## スコープ外(明示)

- `_ShadowColorTex` / `_ShadowBorderMask` / `_ShadowBlurMask` / AO シフト類(lilToon の bake シェーダー自体が無視するため)
- `OutlineAlbedoTint`、`_OutlineTex`(輪郭線色テクスチャ)、`_OutlineFixWidth`(距離補正)
- ShadowRamp 以外の exemplar 共有フィールド(Rim / Specular / Subsurface 等)の per-material 化

## SESSION_ID (for /ccg:execute use)
- CODEX_SESSION: 019f57ce-d77c-7ac3-b09f-e14859fdbf4d
- GEMINI_SESSION: N/A(Gemini API プリペイドクレジット枯渇のため失敗。課金補充後に再実行可能だが、本タスクは純バックエンドのため必須ではない)
