# Editing a SWF

MortisFlash can modify the file that is open: replace images, sounds and binary data, change texts, and change
scripts either as ActionScript (recompiled by the built-in compilers) or as p-code (bytecode listing). This page
describes each kind of change, its constraints, and a complete example. Opening and browsing files is covered in the
[user guide](user-guide.md).

## How changes are applied

Every change follows the same cycle:

1. The change is applied to the tags of the document in memory.
2. The whole SWF is rewritten, then read again. The tree and the previews always show the rewritten file, so what you
   see is exactly what will be played and saved.
3. The item you were working on is selected again, and the game restarts if the game panel is open.

The status bar reports the result, for example "Replacing image 12: done. Click “Play” to test it." or "…: done. The
game has been restarted." If the change fails, the status bar shows "… failed:" followed by the reason, and the
document returns to its state before the change.

After the first change, the window title shows "• modified" and the **Save SWF…** button appears. Changes accumulate
in memory: the file on disk is not modified until you save. There is no undo command; to start over, open the
original file again.

### Saving

Click **Save SWF…** or press `Ctrl+S`. The suggested file name is `<original name>_modified.swf`; save to a new file
rather than over the original.

- The saved file keeps the compression of the original, except that LZMA-compressed files (`ZWS`) are saved
  zlib-compressed (`CWS`), since MortisFlash has no LZMA encoder. Every Flash player accepts `CWS`.
- When the opened file is a projector (`.exe`), the saved file is the SWF only; the projector is not rebuilt.

### Nested SWFs

When you edit a SWF opened with **Open this SWF** (a SWF stored as binary data inside another one), the changes apply
to that inner SWF only, and **← Parent SWF** returns to the parent as it was, discarding them. To put a modified
inner SWF back into its parent:

1. Make the changes in the inner SWF and save it with **Save SWF…**.
2. Click **← Parent SWF**.
3. Select the same binary data item, click **Replace…** and choose the file saved in step 1.
4. Save the parent with **Save SWF…**.

## Replacing images

Select the image, click **Replace…** and choose a PNG, JPEG or GIF file.

- Maximum size: 8191 × 8191 pixels.
- A JPEG file that replaces an opaque image (stored as `DefineBits`, `DefineBitsJPEG2` or `DefineBitsLossless`) is
  stored as it is, as a JPEG. In every other case (PNG or GIF file, or an original image with transparency), the
  image is stored losslessly with its alpha channel (`DefineBitsLossless2`).
- The image keeps its character ID, so everything that displays it shows the new image.
- Keep the original dimensions. Shapes draw a bitmap with their own fill matrix, which is not changed: an image of a
  different size is accepted but appears tiled or cropped.

## Replacing sounds

Select the sound, click **Replace…** and choose an MP3 or a WAV file. The constraints depend on the kind of sound:

| | Event sound (`DefineSound`) | Stream sound (timeline music) |
|---|---|---|
| MP3 | MPEG audio Layer III at 11025, 22050 or 44100 Hz, mono or stereo | Same |
| WAV | Uncompressed PCM, 8 or 16 bits, mono or stereo, at 5512, 11025, 22050 or 44100 Hz | Uncompressed PCM, **16 bits only**, mono or stereo, at 5512, 11025, 22050 or 44100 Hz |

These are the sample rates that Flash supports; other files are rejected with a message giving the reason. An ID3v2
tag at the start of an MP3 file is skipped. To convert a file, use any audio tool, for example ffmpeg:

```
ffmpeg -i input.ogg -ar 44100 -c:a libmp3lame -b:a 128k output.mp3
ffmpeg -i input.ogg -ar 44100 -c:a pcm_s16le output.wav
```

### Stream sounds

Stream sounds are listed under Sounds as "stream — main timeline" or "stream — sprite N". A stream sound is stored as
one block of audio per frame of its timeline, so the replacement is cut accordingly:

- The audio is divided into one block per frame at the movie's frame rate, and each block is placed before the end
  of its frame. The stream header is rewritten as a `SoundStreamHead2` tag.
- If the new sound is longer than the timeline (number of frames divided by the frame rate), the end is cut and the
  status bar says so, for example "The sound lasts 95.0 s but the timeline only 60.0 s (1440 frames at 24 fps): the
  end was cut."
- If it is shorter, the remaining frames have no audio.

## Replacing binary data

Select the binary data item, click **Replace…** and choose any file. Its content replaces the data byte for byte, and
the type shown in the tree is detected again after the reload. Typical uses are level files (XML, JSON, text) and
nested SWFs.

## Editing texts

Select a text in the Texts category. The preview shows an editor, a hint above it, and the rendered text below.
Change the text and click **Apply**.

### Text fields

Text fields (`DefineEditText`, for example dynamic and input fields) are edited as their initial text. For HTML text
fields, the text is Flash HTML: `<b>`, `<font color="#…">`, `<br>`, and so on. If the game's code assigns a text to
the field, that text replaces the initial text when the code runs.

### Static texts

Static texts (`DefineText`, `DefineText2`) are made of glyphs from an embedded font, which brings two limits:

- **Only the characters embedded in the font can be used.** The hint above the editor lists them. Any other
  character is rejected with a message such as: "The font “Arial” does not contain the character “é”: a static text
  can only use the characters embedded in its font."
- **The whole text takes the font, size and color of the original first line**, and starts at its position. Other
  styles of a multi-style text are not kept.

Press `Enter` to start a new line. Line spacing is taken from the original text when it had several lines, otherwise
from the font's metrics. The text's bounds are not recalculated.

## Editing scripts

Select a script and click **Edit**. The editor opens in **ActionScript** mode. Its toolbar contains:

- **Method** (ActionScript 3 only): the method to edit;
- **ActionScript** and **P-code**: the editing mode;
- **Apply and test**: compiles or assembles the text, rewrites the SWF and restarts the game if it is running;
- **Cancel**: returns to the script view without changing anything.

Choosing another method or switching between ActionScript and P-code reloads the text from the current SWF: changes
that have not been applied are lost. Copy them first if you need them. `Tab` inserts a tab character.

### ActionScript 3

ActionScript 3 code is edited one method body at a time.

- The **Method** list contains the methods that have a body: "constructor", the instance methods (accessors are
  prefixed with `get` or `set`), the static methods (prefixed with `static`), "static initializer" and
  "script initializer".
- The signature of the method is shown above the editor, for example
  `function onPlus5(param1:MouseEvent) : void { … }`. It is read-only: the name, parameters, return type and
  modifiers stay unchanged. Only the statements between the braces are edited. Fields, methods and classes cannot be
  added from the editor.
- The parameters are available under the names shown in the signature.
- Names that are not local variables or parameters are resolved at run time by name, through the namespaces of the
  class (private, protected, internal) and every package of the bytecode, as the Flex compiler does for untyped code.
  `import` statements are therefore not needed; they are accepted and ignored. Private members of other classes stay
  inaccessible.

### ActionScript 1 and 2

ActionScript 1 and 2 scripts are edited as a whole:

- frame scripts (`DoAction`) and initialization actions (`DoInitAction`);
- clip event handlers and button handlers, including their `onClipEvent(...) { }` or `on(...) { }` wrapper, which
  must be kept;
- ActionScript 2 classes (under `__Packages`), shown and edited as a `class Name extends Base { … }` declaration and
  compiled back to the prototype-based code that Flash uses.

The compiler targets the SWF version of the file. The following are rejected because they are ActionScript 3 only:
`for each`, rest parameters (`...args`), and the `is`, `as` and `in` operators (`for (name in object)` loops are
supported). A `try` statement can have a single `catch` clause.

### Compile errors

When the code does not compile, the error appears in red above the editor as "line N: message", the cursor moves to
that line, and your text is kept. In ActionScript mode the message ends with a suggestion to switch to P-code when the
construct is not supported. For ActionScript 3, line numbers count from the first line of the method body in the
editor. A few errors, such as the closure limitation below, carry no line number.

### What the compilers do not support

The compilers cover the language used by games: expressions, all statements, `try`/`catch`/`finally`, functions and
closures. They do not support:

- E4X syntax: attribute access (`xml.@name`) and descendant access (`xml..item`);
- `Vector` literals (`new <int>[1, 2, 3]`);
- `with` statements in ActionScript 3;
- in ActionScript 3, nested functions that use local variables of the enclosing method ("closures over locals are not
  supported yet"); nested functions that do not use them are compiled;
- the `§§` placeholders written by the decompiler for code it could not rebuild (`§§goto(...)`, `§§pop()`, and so
  on): the error says "'§§…' marks code the decompiler could not rebuild; edit this script as p-code instead".
  Obfuscated names written as `§name§` are accepted;
- labels on statements other than loops and `switch`.

For these cases, edit the script in P-code mode.

### P-code mode

P-code mode edits the bytecode listing directly. It is always available, including for scripts that the decompiler
could not fully rebuild. For ActionScript 3 it covers the selected method; for ActionScript 1 and 2, the whole script.

Common rules:

- One instruction per line. Instruction names are not case-sensitive. Text after `//` is a comment; empty lines are
  ignored.
- A label is a name followed by a colon at the start of a line. The listing generates labels from the original
  addresses: `ofs0012:` for ActionScript 3 and `loc00A4:` for ActionScript 1 and 2. They are ordinary names: you can
  add your own, such as `retry:`, and jump to them.
- A jump to `ofsXXXX` or `locXXXX` with no matching label uses that address as is. Avoid this after inserting or
  removing instructions, since the addresses move.
- Strings are written between double quotes, with the escapes `\n`, `\r`, `\t`, `\b`, `\f`, `\"` and `\xHH`.
- Errors are reported as "line N: message", like compile errors.

#### ActionScript 3 p-code

- Exception handlers are listed first, as
  `try from ofsA to ofsB target ofsC type <multiname> name <multiname>`.
- Names are written as multinames, for example `QName(PackageNamespace("flash.events"),"MouseEvent")`,
  `Multiname("score",[PackageNamespace(""),PrivateNamespace("Game",#4)])` or `*` (any name). A private namespace, and
  any namespace that has a duplicate, carries its constant pool index (`#4`) because two private namespaces with the
  same name are different to the virtual machine.
- `#mnN` refers directly to entry N of the multiname pool. The compiler emits this form.
- Strings, numbers, namespaces and multinames that are not in the constant pool yet are added to it; existing entries
  are reused.
- The maximum stack size, scope depth and number of local registers are recomputed. They are never set below the
  original values.
- `pushbyte` takes a value from -128 to 127 and `pushshort` a value from -32768 to 32767; values outside these ranges
  wrap around. Use `pushint` for larger values.
- A `maxstack … locals … scope …` header line, as printed by `mflash scripts --pcode`, is ignored.

#### ActionScript 1 and 2 p-code

- Instructions are actions with their operands, for example `Push "score", 5`, `GetVariable`, `If loc0012`,
  `Jump loc00A4`.
- `Push` accepts strings, integers, decimal numbers, `NaN`, `Infinity`, `-Infinity`, `true`, `false`, `null`,
  `undefined` and registers (`register1`).
- The bodies of `DefineFunction`, `DefineFunction2` and `With` follow their action between `{` and `}` lines. `Try`
  is followed by its body, then optional `catch {` and `finally {` blocks. A `DefineFunction2` line has the form
  `DefineFunction2 "name" (r1:a, b) registers 3, flags 0x002A`.
- Constant pool: the listing shows pushed strings by value. When assembling, a pushed string that appears in the most
  recent `ConstantPool` action is stored as a reference to the pool; any other string is stored in the `Push` action
  itself. New strings therefore work without editing the `ConstantPool` line.
- A nested block is limited to 64 KB, and a jump to 32 KB in either direction.

## Worked example

This example uses `debug_game_as3_scoring.swf` from the JPEXS test suite. Its `MainTimeline` class keeps a score in a
text field; the button wired to the `onPlus5` method adds 5 to it. The goal is to make it add 100.

1. Open `debug_game_as3_scoring.swf`.
2. In the tree, expand **Scripts** > `as3_scoring_fla` and select `MainTimeline.as`. The class contains, among
   others, the methods `getScore`, `setScore`, `onPlus5`, `onMultiply2`, `onReset` and `frame1`.
3. Click **Edit**, then choose `onPlus5` in the **Method** list. The signature line reads
   `function onPlus5(param1:MouseEvent) : void { … }` and the editor contains:

   ```actionscript
   var score:int = this.getScore();
   score += 5;
   this.setScore(score);
   trace("plus 5");
   ```

4. Change `score += 5;` to `score += 100;` (and, if you like, the `trace` message to `"plus 100"`).
5. Click **Apply and test**. The status bar shows "Editing MainTimeline.as: done.", and the script view shows the
   class with the new code.
6. Click **▶ Play** (if the game panel was already open, the game has restarted on its own). Click the game's
   **Plus 5** button: the score, which otherwise decreases by one every second, increases by 100, and the console
   shows the `trace` message.
7. Press `Ctrl+S` and save the result, for example as `debug_game_as3_scoring_modified.swf`.

The same change in P-code mode: select `onPlus5`, switch to **P-code**, and replace `pushbyte 5` (the instruction
just before `add`) with `pushbyte 100`.

In the same file, the "script initializer" method contains a `§§newclass` placeholder: it cannot be compiled from
ActionScript and must be edited in P-code mode.

The ActionScript 2 version of the game, `debug_game_as2_scoring.swf`, works the same way: the handler is the script
`DefineButton_6` > `on(press)`, edited as a whole, wrapper included. In P-code mode, the value is the `Push 5`
instruction before `Add2`.

## Good practices

- Keep a copy of the original file, and save each modified version under a new name.
- Make one change at a time and test it right away: **Apply and test** restarts the running game, and `F5` restarts
  it at any time. Watch the console for player errors.
- Add `trace()` calls to follow what the code does; their output appears in the console.
- Prefer ActionScript mode for readability, and use P-code mode for what the compilers do not support.
- In P-code mode, keep the stack balanced along every path. The assembler checks the syntax, not the logic: invalid
  bytecode is only detected by the player.
- Keep the dimensions of replaced images and the formats listed above for sounds.

## See also

- [User guide](user-guide.md)
- [Command-line tool](command-line.md)
- [Architecture](architecture.md)
- [Building](building.md)
- [Project README](../README.md)
