# Worn item graphics

`DefineItem(...).Wearable(...)` adds original procedural graphics to a cloned armor or weapon item. The item grants, equips and syncs as a normal clone. The client attaches a skinned mesh and hides the donor renderers named by the sketch. Existing equipment supplies fitting references; its triangles are not copied into the garment.

The working example is [Progenitor.RobeSet](../samples/Progenitor.RobeSet/README.md).

## Appearance slots and colors

Chest, gloves, legs, footgear and cloak graphics follow the cosmetic slot when its item ID is populated, including vanilla cosmetics. Without an override they follow the stat slot. A hidden or broken selected item suppresses the overlay. An empty cosmetic slot does not override the stat item. Headgear, weapons and grimoires use their existing equipment slots. The robe is verified live in the cosmetic chest slot with an empty stat chest slot. Other slots, vanilla cosmetic overrides and hidden/broken transitions still require live qualification. A registered custom slot ID takes precedence over a client item entity that retains its vanilla donor ID.

Authored colors stay fixed unless the garment declares `surfacePalette`, for example `{"cloth":"primary","gold":"metal"}`. Supported channels are `primary`, `accent`, `neutral` and `metal`. Unmapped surfaces, including an unmapped black face, retain their authored colors. The client reads `TransmogPaletteData` through `ManagedDataRegistry.TryGet`, subtracts the slot key's `StartIndex` from `TransmogIndex`, and checks the resulting choice index before applying mapped colors. Missing or invalid palette data restores authored colors; the native default selection is not an authored-color reset. Materials are owned by each worn garment and can update without rebuilding its mesh. **Verified live, limited:** choosing the top-right native swatch changed the robe chest to red. The hood stayed blue in that fixture because its palette source was Headgear. The picker X hides equipment. Explicit selection of the desired authored blue remains unresolved; broader picker and fallback behavior is unqualified. The earlier crashing native utility wrapper is no longer used.

By default each garment reads the palette of its own visible equipment slot. Set `wearablePaletteSource` to `Chest` on the Blender garment object to persist chest inheritance as `paletteSource` in exported JSON. Slot names are case-sensitive; invalid or unknown fields fail validation. An authored hood can instead follow the chest palette: `WearableGraphics.FromAuthoredJson(WearableSlot.Headgear, json, hideNames).WithPaletteSource(WearableSlot.Chest)`. This changes only mapped surfaces; the unmapped black face stays fixed. The source chest's occupied cosmetic slot takes precedence over its stat slot. **Test-verified:** the API preserves the original recipe and rejects an invalid source slot. **Verified live, limited:** a material capture shows identical hood/chest cloth colors and an unchanged black face.

Use `.WithAuthoredColors()` to retain authored colors even when native palette data exists. Blender objects can persist this choice with `wearablePaletteMode = "authored"`, exported as `paletteMode`; omitted or `"native"` preserves native selection. This is an author-level recipe choice, not a new native picker swatch or per-player preference.

## Blender-authored garments

The [Blender workflow](../tools/rig-reference/README.md) exports complete body/rig references, fits explicitly selected original garment regions, transfers weights without moving loose panels, checks multiple captured poses, and exports garment JSON. Each successful build also saves `authoring.blend` and a resumable `outfit.json` naming the built objects; rebuild from that manifest into a fresh output directory. `WearableGraphics.FromAuthoredJson(slot, json, hideNames)` retains the authored skin weights, normals, UVs and cloth/gold/void surfaces. Optional `surfaceColors` stores per-garment shader RGBA values; missing colors preserve legacy shared defaults. It refuses incompatible bone paths/rest matrices; one asset targets one rest skeleton. Use `FromAuthoredJsonVariants(slot, jsonVariants, hideNames)` to select the first asset whose complete bone paths and rest matrices match the wearer. Variants share palette source/mode but retain their own surface colors and mappings. A body with no compatible asset remains unqualified; the runtime does not automatically reshape clothing or supply cloth physics. Keep source `.blend` files and captures local; distribute only the original garment package with the outfit plugin.

Complete female body and four-piece robe capture/import are verified live within 0.44 mm at a sampled pose; fitting, pose replay and export have synthetic Blender tests. An original authored chest also loaded in game and reimported within 0.35 mm. Combat coverage remains unqualified. See the workflow for qualification evidence. The procedural API below is independent of this path.

## Register

```csharp
ctx.Content.DefineItem("robes.chest")
    .CloneOf("Item_Chest_T09_Dracula_Scholar")
    .Named("Tassel Robe")
    .Described("A robe with a gold tassel belt.")
    .Wearable(WearableLooks.RobeChest())
    .Register();
```

`WearableGraphics.Define(slot, sketch => ...)` records a sketch. `WearableLooks` supplies `RobeChest`, `RobeHood`, `RobeGloves`, `RobeShoes`, and an independent `Staff` recipe. The built-in Guide outfit registers and grants only the four clothing pieces; its source assets remain in `samples/Progenitor.RobeSet`.

| Shape | Contract |
|---|---|
| `Bodice(surface)` | Original close-fitting torso contours measured in each direction from body samples, with clearance between adjacent rings; a measured pelvis seam meets `Skirt`. |
| `Skirt(surface)` | Continuous flared skirt with measured upper-hip clearance, shared waist and ring vertices, blended leg weights, and a sewn gold hem. Skinning approximates drape; it is not cloth simulation. |
| `RobeBody(surface)` | Basic connected torso and skirt with a shared waist and two-leg lower rings. The tailored sample uses `Bodice` and `Skirt`. |
| `Glove(wrist, elbow, surface)` | Connected forearm, wrist and palm with five articulated, closed finger envelopes. Compatible body samples fit the contours and fingertip lengths. Requires three joints per finger. The four-argument overload accepts an optional cuff surface. |
| `Sleeve(shoulder, elbow, wrist, surface)` | Continuous shoulder-to-wrist surface with locally measured upper-arm clearance, closely spaced shoulder rings, shared elbow rings and an inward shoulder cap overlapping the bodice. |
| `Shoe(ankle, ball, radius, surface)` | Closed shoe extending toward the ball of that foot. Both joints are required. |
| `Loft(from, to, radiusFrom, radiusTo, surface, followEnd)` | Without `followEnd`, the whole tube follows `from`, retaining its horizontal center and using `to`'s height. With `followEnd`, it follows the limb and weights the far ring to `to`. |
| `Ring(bone, radius, yOffset, surface)` | A band around a bone. With `Fit` at the pelvis and complete body measurements, follows the shared waist contour without radial flare. |
| `Tassels(bone, radius, count, length, surface, yOffset)` | With `Fit` at the pelvis after `Skirt`, narrow tapered ribbons share the skirt surface, without raised strips. Other uses retain hanging strips; `yOffset` places their starting ring. |
| `Cowl(head, radius, cloth, face)` | Angular closed hood with an asymmetric pointed crown, hanging side folds and a narrow opening joined to a curved, recessed opaque interior. |
| `Cap(bone, awayFrom, length, radius, surface)` | A tube extending away from another joint. Use `Glove` or `Shoe` for the tailored pieces. |
| `Pole(bone, up, forward, radius, surface)` | A staff rising from the hand. |
| `Disc(bone, radius, forward, surface)` | A filled disc in front of a bone. |
| `Models(male, female)` | Records optional model names for diagnostics. It does not retarget the cloned item's model fields. |
| `Hide(prefixes...)` | Donor renderer prefixes to hide while equipped. |

`WearableSketch.Fit` (`-1`) requests a fitted radius. Shoulder span sets the scale; adjacent torso lofts use the same fitted radius at a shared named joint. Radius fitting alone does not establish body clearance.

`Cloth` and `Gold` use HDRP/Lit; `Void` uses unlit black. Materials are double-sided. Missing required HDRP shaders prevent attachment and produce a diagnostic.

Custom wearable plugins must load on both sides. The Guide outfit itself ships inside Progenitor Core and needs no separate plugin. `Wearable` is not a wire field: the client needs the same registration to build the overlay. A client without the plugin shows the donor.

### What every worn item gets

These apply to every registered wearable, authored (`FromAuthoredJson`), sketched (`Define`) or borrowed (`FromNativeRenderer`), with nothing to declare. A garment author does not need to handle them, and should not work around them:

- **Hidden while the wearer's body is hidden.** When the game hides the character's own body (every native `Body_GEO` under the model disabled or inactive, for example a dead player waiting on the respawn choice), the garment and any authored overlay (such as a face insert) stop drawing, and they return when the body does. Without this, other players saw the clothing floating around an invisible body. Unverified: whether any native cosmetic disables `Body_GEO` on a living character, which would hide garments while it is worn. Observed live 2026-10-09: a male wearer of the Guide set was killed; on another client nothing was drawn where he lay, and his garments returned after the respawn ([Model metadata and donor visibility](#model-metadata-and-donor-visibility)).
- **Game overlay effects.** While the game draws the body with only its overlay effect materials (a hit or status flash), owned garments take the same effect, and a fixed black insert hides for its duration.
- **Clean removal.** Unequipping, a model replacement and a Core uninstall destroy the owned renderers and restore any donor renderer the garment hid (`Hide`/`hideNames`), with reference counting.
- **Colors.** A garment with `surfacePalette` takes the dye a player picks (and Dye All's color), on every client; see [Appearance slots and colors](#appearance-slots-and-colors).

## Native renderer reuse

`WearableGraphics.FromNativeRenderer(slot, exactRendererName, hideNames)` borrows a skinned renderer supplied by the equipped donor. It creates an owned renderer and material copies while retaining the game-owned mesh, bones and local transform. It never exports or destroys the native mesh. A missing or ambiguous donor defers attachment. Extra material passes added by native effects (such as Blood Moon) are retained; fewer materials than submeshes defers attachment.

Use `WithNativeColorOverride(materialIndex, shaderProperty, paletteChannel, r, g, b, a)` for a palette-mapped shader color with an explicit fallback; `WithNativeFixedColor(materialIndex, shaderProperty, r, g, b, a)` keeps one color fixed. `WithPaletteSource(WearableSlot.Chest)` also applies to native recipes. Inspect actual shader properties first: the named property must exist and be a Color. Native textures remain and multiply tint, so matching palette inputs can look different from plain authored cloth.

`WithNativeWhiteTexture(materialIndex, shaderProperty)` replaces a verified texture property with the built-in white texture on the owned material clone. Use this explicitly when baked albedo would distort palette colors. `WithAuthoredOverlay(json)` adds an original, fixed-black, void-only skinned insert; it validates the captured bind reference and owns that additional mesh. Neither operation modifies the source material or game mesh.

**Verified live, limited:** the female Razer hood retained its native shape, followed the selected pale chest color and displayed a black face after neutralizing its base texture and adding the original insert. Broad pose, male-body and lifecycle qualification remain incomplete. This path does not export native assets.

## Fitting and bones

Geometry is constructed from compatible authored donor bind matrices, not the animation pose at equip time. Matching bone identities and bind transforms are retained for skinning. Sparse skin palettes may use co-located roll-bone fallbacks for procedural pivots, retaining the actual skin bone and bind matrix; this is not full control-joint retargeting. A changed same-ID graphics recipe rebuilds an already-equipped overlay on plugin reload. An incomplete compatible reference defers attachment; it does not substitute the current animated pose or move the skeleton.

Compatible body vertices provide torso and hip clearance samples. Native triangles, topology and UVs are not reused. Samples constrain the original bodice and upper-skirt contours and are counted as `fitPoints=` in attachment diagnostics. A complete measured waist contour is shared by bodice, skirt and fitted pelvis belt; incomplete measurements retain the procedural fallback. A positive count is evidence that fitting inputs were found, not proof of visual clearance.

The rig uses `Neck_JNT`, `Chest_JNT`, `Pelvis_JNT`, `Head_JNT`, shoulder, elbow, wrist, knee, ankle and ball joints. Supported aliases include low spine `Gut`/`Spine1`/`Spine2`, chest `Ribcage`, neck `NeckBase`/`Neck01`, pelvis `Hips`, shoulders `Clavicle`/`UpperArm`, wrists `Hand`, and ankles `Foot`. The low spine is not an alias for the chest.

Gloves require `Left_` or `Right_` chains `Index01`–`03`, `Middle01`–`03`, `Ring01`–`03`, `Pinky01`–`03`, and `Thumb01`–`03`, each suffixed `_JNT`. The shipped middle-thumb spelling `Thum02_JNT` is accepted. Missing required joints drop that shape and appear in `missing=` diagnostics.

Procedural geometry supplies up to two normalized bone weights per vertex as a fallback. At attachment, compatible native armor supplies reference positions and skin influences for the relevant clothing slot; compatible body samples are the fallback source. The close-fitting chest above the chest joint uses body weights first so the sleeve and bodice overlap follows the underarm. Transferred weights are normalized to at most four influences per generated vertex. Headgear and weapons retain their procedural weights. Source triangles, contours and UVs are not copied.

Readable references supply authored positions directly. For an unreadable reference, the client can recover rest positions from a `BakeMesh` snapshot by inverting its weighted skin transforms. This path requires unit renderer scale and no blendshapes; unsupported or unavailable references leave the body/procedural fallback in place. A posed snapshot is never directly used as an authored rest mesh.

Body skin references remain available when armor covers only part of the garment. Without a usable leg-armor reference, chest-garment vertices below the pelvis use compatible body weights instead of reaching upward to torso armor. Attachment diagnostics report each source and body-fallback usage.

Shared sleeve and skirt rings prevent split seams. Separately generated bodice and skirt waist rings match positions and fallback pelvis weighting. These structural checks and reference-weight transfer do not establish animation acceptance.

## Model metadata and donor visibility

`Models` is diagnostic only. Reflected field names are not a verified native memory-layout contract, so the implementation leaves model references unchanged. The cloned item's normal equipment spawning continues; the procedural overlay supplies the new appearance.

Hide prefixes are case-insensitive and tolerate the game's equipment prefixes. Body, face, eye, brow, teeth, ear, skin and eyelash renderers are protected. Hair is also protected unless a Headgear piece explicitly includes the exact `Hair` hide token; the robe hood does so. Hide ownership refreshes while worn and restores the previous enabled state when released. The hood's black recess covers the face without disabling its renderer.

`WearableAppearance` checks equipment after native presentation each client frame, checking reference changes every eight frames, and creates one `progenitor-wear-<guid>` skinned renderer per matching slot. Changes to native renderer, mesh and bone readiness rebuild attachments after two consistent checks; animation and renderer visibility do not trigger rebuilds. Reference rebuilds stage a complete replacement before retiring the working appearance. A borrowed native renderer temporarily using only `Shader Graphs/Sh_Overlay` materials retains its geometry and borrows those game-owned effect materials. Authored garments follow the same all-overlay material state on `Body_GEO`, applying the primary effect across their submeshes. The fixed black hood insert is hidden during the effect; owned resting materials and the insert return afterward. Other shader transitions still require qualification. Owned renderers also follow the game's visibility of the wearer: when every native `Body_GEO` under the model is disabled or inactive, the garment and its insert stop drawing, and they return when the body does. `Body_GEO` is protected from wearable hides, so its state is the game's decision. **Observed live (renderer readback, before this rule):** on another client, a dead player waiting on the respawn choice had a disabled `Body_GEO` and native leg renderer while every overlay stayed enabled, which showed a headless wearer with a closed hood. **Test-verified:** the visibility decision. **Observed live (2026-10-09, captures from another client):** after the fix, the dead wearer's three `progenitor-wear-*` renderers read disabled with `Body_GEO`, nothing was drawn where he lay, and the garments and hood returned on respawn; the hood also drew correctly on the living male body. It is also unverified whether any native cosmetic or form disables `Body_GEO` on a living character; if one does, Progenitor garments hide while it is worn. The body renderers are rediscovered on the eight-frame reference check, and each change logs one debug-level `Wearables: concealing|showing overlays on model <id>` line per wearer. Unequip, model replacement and Core uninstall destroy overlays and restore hidden donor renderers with reference counting. Core uninstall also removes the frame hook.

## Verification scope

Pure tests cover mesh topology and winding, continuous sleeve seams, fitted torso clearance, procedural and transferred weights, forward shoes, closed fingertips and the angular recessed hood. Run from the repository root:

```powershell
dotnet test tests/Progenitor.Tests/Progenitor.Tests.csproj --filter FullyQualifiedName~Wearable
```

The latest four-piece clothing build is deployed and all four attachments have been observed on a standing female standard-form character in the isolated test client. Attachment diagnostics confirm body clearance samples and skin-weight transfer for the chest, gloves and shoes; the hood intentionally retains procedural weights. Standing appearance has been inspected, and female coverage and shape were confirmed after repeating the tested combat moves with attention to the shoulders, waist and thighs. This acceptance is limited to those moves; male fit, broader animations and equipping while animated remain unqualified.

This path does not close new-content readiness. A spell and NPC with their own graphics remain open. It does not execute the content-archive loader and is not a general authored-mesh importer.

The Guide sample embeds original authored assets so rebuilding preserves accepted geometry and palette mappings. Its ArmorBaseline(1) recipe clears inherited item bonuses and set association on both sides, and every piece is `.BloodBound().Indestructible()`: kept on death and never worn out, however it was obtained (source-checked, built, game-free tested; not live-verified). Wearable visibility is reconciled after native presentation each frame; pending attachments briefly conceal owned donors before bind readiness, restoring them after a bounded timeout if attachment fails.
