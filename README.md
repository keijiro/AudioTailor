# Audio Tailor

**Audio Tailor** is a Unity Editor tool for processing audio clips.

## How to Use

Open the window from **Window &gt; Audio &gt; Audio Tailor**, or click the
**Audio Tailor** button that appears in the Inspector header when an audio clip
is selected. Enable the desired processing options, then click **Process** to
preview the result. Once satisfied, click **Save** to write the processed audio
back to the source file.

## Features

### Trim Silence

Removes silence from the beginning and end of the clip. The silence detection
threshold and the release threshold are each configurable in decibels. A short
fade-out is applied at the cut point to prevent clicks.

### Normalize

Scales the clip to a specified peak level in decibels, which is useful for
evening out volume differences between clips.

### Make Loop

Creates a seamless loop by blending the head and tail using an equal-power
crossfade. The **Pre-trim** setting discards a short portion at each edge
before looping, which is useful when the attack or decay of a sound should
not be part of the loop body.

### Convert to Mono

Mixes all channels down to mono by averaging, reducing memory usage when
stereo separation is not needed.

## How to Install

The Audio Tailor package (`jp.keijiro.audio-tailor`) can be installed via the
"Keijiro" scoped registry using Package Manager. To add the registry to your
project, please follow [these instructions].

[these instructions]:
  https://gist.github.com/keijiro/f8c7e8ff29bfe63d86b888901b82644c
