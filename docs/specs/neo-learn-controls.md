# Neo Learn — Appendix A: controls and Explain text

Part of [neo-learn.md](neo-learn.md). Every machine has 12 controls: 6 core (A2) and 6 global (A1). `id` is stable (the agent and presets use it); **Name** is the label; the two text columns are the Explain strip's **How** and **Sound** lines. Ranges are the real-world values the control shows; §5.2 of the spec says how they map onto Buzz parameters.

## A1. Global controls (all 13 machines)

Every Neo Learn machine has these 6 controls on the right of the control deck:

| ID | Display Name | Range / Unit | `explainMechanism` | `explainSound` |
| :--- | :--- | :--- | :--- | :--- |
| `env_attack` | **Attack** | `0.5 ms – 5000 ms` | Sets the time required for the note's amplitude (and routed envelope modulation) to ramp from silence to full peak after a note-on trigger. | Low values create immediate, percussive plucks; high values produce slow, swelling pads. |
| `env_decay` | **Decay** | `5 ms – 8000 ms` | Controls how long it takes for the signal to drop from peak level down to the sustain level while a key remains held. | Short decay makes notes snappy and tight; longer decay gives a gradual piano- or bell-like fade. |
| `env_sustain` | **Sustain** | `0% – 100%` | Defines the steady-state volume level maintained for as long as a note is held after the Attack and Decay stages complete. | At 0% the synth behaves like a plucked or struck instrument; at 100% it holds full organ-like volume. |
| `env_release` | **Release** | `5 ms – 10000 ms` | Sets the time required for the sound to fade from its current level to complete silence after the note is released. | Short release stops notes abruptly; long release leaves an ambient tail that blends chords together. |
| `unison_detune` | **Unison / Spread** | `0% – 100%` | 0% plays one voice per note; above 0% each note plays 5 detuned, stereo-spread voices (3 in Additive, Granular and Spectral), with detune (up to ±50 cents) and width growing with the value. | Thickens thin waveforms into wide, chorus-like stereo leads and pads. |
| `output_gain` | **Output** | `-36 dB – +12 dB` | Sets the level after synthesis. There is no limiter (NeoBuzz mixes in floating point); a clip light shows when the output passes 0 dBFS. | Controls overall loudness; keep the clip light dark when the next machine expects levels at or below 0 dBFS. |

---

## A2. Core controls per machine

### A2.1 Neo Learn Subtractive
* **Category:** Classic
* **Visualizer:** Live waveform oscilloscope overlaid with the interactive filter frequency response curve (X = `filter_cutoff`, Y = `filter_resonance`).

| # | ID | Name | Range / Unit | `explainMechanism` | `explainSound` |
| :- | :--- | :--- | :--- | :--- | :--- |
| 1 | `filter_cutoff` | **Cutoff** | `20 Hz – 20 kHz` | Sets the corner frequency of the 24 dB/octave low-pass filter above which upper harmonics are attenuated. | Lowering it makes the sound dark, muted, and warm; raising it lets bright, buzzy harmonics through. |
| 2 | `filter_resonance` | **Resonance** | `0% – 100%` | Feeds a narrow band of frequencies at the cutoff point back into the filter input to create a resonant peak. | Adds a sharp, vocal, or whistling edge to filter sweeps and emphasizes the cutoff frequency. |
| 3 | `wave_morph` | **Wave Shape** | `0% – 100%` | Continuously crossfades the primary oscillator through Sine (0%), Triangle (33%), Sawtooth (66%), and Pulse (100%). | Transitions the raw tone from pure and flute-like to bright and brassy, then hollow and nasal. |
| 4 | `sub_mix` | **Sub Osc** | `0% – 100%` | Blends in a phase-locked square wave oscillator pitched one octave below the played note. | Adds deep low-end weight and foundation for basslines and massive leads. |
| 5 | `filter_env_amt` | **Env to Filter** | `-100% – +100%` | Scales how strongly the main ADSR envelope sweeps the filter cutoff frequency on each note. | Positive values create punchy, brassy, or plucked filter transients that mellow out as the note decays. |
| 6 | `analog_drive` | **Drive** | `0% – 100%` | Applies soft hyperbolic tangent (`tanh`) saturation before the filter and subtle per-voice pitch drift. | Compresses peaks, adds warm harmonic grit, and mimics vintage analog hardware instability. |

---

### A2.2 Neo Learn Additive
* **Category:** Classic
* **Visualizer:** 64-bar real-time harmonic partial spectrum graph (X = `spectral_tilt`, Y = `odd_even_balance`).

| # | ID | Name | Range / Unit | `explainMechanism` | `explainSound` |
| :- | :--- | :--- | :--- | :--- | :--- |
| 1 | `spectral_tilt` | **Brightness Tilt** | `-24 to +6 dB/oct` | Adjusts the amplitude roll-off slope across all active upper sine partials relative to the fundamental. | Tilting down yields warm, flute- or organ-like tones; tilting up produces bright, glassy textures. |
| 2 | `odd_even_balance` | **Odd / Even** | `-100% – +100%` | Attenuates either even-numbered harmonics (left) or odd-numbered harmonics (right) above the fundamental. | Odd only sounds hollow and woody, like a clarinet; even only sounds thin and octave-like; in the middle the full series sounds rich, like a saw. |
| 3 | `partial_count` | **Partials** | `1 – 64` | Sets the hard ceiling on how many harmonic sine waves are synthesized above the fundamental pitch. | At 1 you hear a pure sine wave; increasing the count progressively builds harmonic complexity. |
| 4 | `inharmonicity` | **Inharmonicity** | `0% – 100%` | Stretches upper partial frequencies away from integer multiples ($f_n = n f_0 \sqrt{1 + B n^2}$). | Transforms pure harmonic organ tones into stiff piano strings, chimes, and metallic bells. |
| 5 | `harmonic_comb` | **Spectral Comb** | `0% – 100%` | Applies a sinusoidal amplitude notch mask across the 64 partials. | Carves hollow, phaser-like vocal formants and resonant notches into the harmonic spectrum. |
| 6 | `partial_shimmer` | **Shimmer** | `0% – 100%` | Introduces independent low-frequency amplitude and micro-pitch drift to upper partials. | Creates a lush, living, choir-like movement across sustained chords. |

---

### A2.3 Neo Learn FM
* **Category:** Classic
* **Visualizer:** Split view of the phase-modulated time-domain waveform and generated sideband spectrum (X = `mod_ratio`, Y = `fm_index`).

| # | ID | Name | Range / Unit | `explainMechanism` | `explainSound` |
| :- | :--- | :--- | :--- | :--- | :--- |
| 1 | `mod_ratio` | **Mod Ratio** | `0.25x – 16.0x` | Sets the frequency ratio of the primary modulator oscillator relative to the audible carrier oscillator. | Whole numbers (1x, 2x, 4x) yield musical, woody, or electric piano tones; fractional ratios create clangorous bells. |
| 2 | `fm_index` | **FM Depth** | `0% – 100%` | Controls the amplitude of the modulator feeding the carrier's phase input, determining sideband density. | At 0% the synth is a pure sine wave; raising it adds glassy harmonics up to aggressive metallic growls. |
| 3 | `mod_decay` | **Mod Decay** | `5 ms – 5000 ms` | Sets the decay time of the dedicated envelope that controls FM Depth over the course of a note. | Short decay creates a bright metallic transient that quickly settles into a warm body (classic EP and FM bass). |
| 4 | `feedback` | **Feedback** | `0% – 100%` | Routes the modulator's output back into its own phase input. | Pushes the modulator from a smooth sine wave into a gritty sawtooth or chaotic digital noise. |
| 5 | `op3_harmonic` | **Metallic Edge** | `0% – 100%` | Blends in a high-ratio third operator ($M_2$) with a fast transient envelope. | Adds glassy tine click for electric pianos or sharp vocal bite for modern FM basslines. |
| 6 | `vel_sensitivity` | **Velocity to FM** | `0% – 100%` | Scales the maximum FM Depth and Metallic Edge based on how hard a MIDI note is struck. | Soft notes play warm and rounded while hard notes bark with bright FM overtones. |

---

### A2.4 Neo Learn Wavetable
* **Category:** Classic
* **Visualizer:** 3D isometric wavetable terrain showing the active waveform slice moving across the table's frames (X = `wt_position`, Y = `phase_warp`).

| # | ID | Name | Range / Unit | `explainMechanism` | `explainSound` |
| :- | :--- | :--- | :--- | :--- | :--- |
| 1 | `wt_position` | **Frame Pos** | `0% – 100%` | Selects and interpolates the active single-cycle waveform across the frames of the loaded table. | Sweeps smoothly through changing timbral shapes, from dark fundamentals to complex harmonic structures. |
| 2 | `phase_warp` | **Phase Warp** | `-100% – +100%` | Bends the phase read within each cycle: left of centre pinches it (Bend), right of centre hard-syncs it (Sync). | Pinches the waveform for nasal hard-sync tones or hollow pulse-width effects regardless of the selected table. |
| 3 | `table_select` | **Wavetable** | `Enum (11)` | Selects one of Dye Essence's tables (`Basic`, `PWM`, `Harmonics`, `Formants`, `Sync sweep`, `FM sweep`, `Organ`, `Resonant`, `Digital`, `Grit`) or `User`, a table you load. | Changes the fundamental sonic palette and harmonic progression of the oscillator. |
| 4 | `scan_speed` | **Motion Rate** | `0.05 Hz – 15 Hz` | Sets the speed of the internal LFO/envelope generator that automatically moves `wt_position`. | Controls whether the timbre evolves slowly like a cinematic pad or wobbles rapidly like a modulated bass. |
| 5 | `scan_depth` | **Motion Depth** | `-100% – +100%` | Sets the travel distance and direction of the automatic wavetable frame scanner. | At 0% the wavetable frame remains static; higher values animate the waveform across the table automatically. |
| 6 | `sub_anchor` | **Sub Anchor** | `0% – 100%` | Blends in a clean, unwarped fundamental sine wave at the root pitch. | Keeps the low end solid and musical even when the main wavetable is heavily warped or bright. |

---

### A2.5 Neo Learn Granular
* **Category:** Sample
* **Visualizer:** Source waveform display with live glowing particles indicating each grain's position, length, and pan (X = `sample_position`, Y = `grain_size`).

| # | ID | Name | Range / Unit | `explainMechanism` | `explainSound` |
| :- | :--- | :--- | :--- | :--- | :--- |
| 1 | `sample_position` | **Scan Position** | `0% – 100%` | Moves the center playhead location from which audio grains are extracted in the source recording. | Scrubs through different timbral moments of the source audio as if freezing time at that spot. |
| 2 | `grain_size` | **Grain Size** | `5 ms – 500 ms` | Sets the duration of each individual audio slice extracted from the buffer. | Very short grains (< 25 ms) sound buzzy and robotic; long grains (> 150 ms) create smooth, lush ambient clouds. |
| 3 | `grain_density` | **Density** | `1 – 64 grains/s` | Controls how many new grains are triggered per second per active voice. | Low density produces sparse, stuttering rhythmic droplets; high density fuses grains into a continuous pad. At most 10 grains overlap per note; beyond that the oldest fade out. |
| 4 | `position_spray` | **Time Spray** | `0% – 100%` | Randomizes each grain's start offset around the main `sample_position` playhead. | Eliminates repetitive looping buzz and smears the recording into an organic, non-repeating texture. |
| 5 | `pitch_shimmer` | **Pitch Shimmer** | `0% – 100%` | Introduces micro-pitch randomization and gradually adds grains pitched +12 semitones (one octave up). | Adds a crystalline, angelic octave glow above ambient pads. |
| 6 | `grain_shape` | **Grain Contour** | `0% – 100%` | Morphs the amplitude window of each grain from sharp trapezoid edges (0%) to smooth Gaussian curves (100%). | Low values sound clicky and glitchy; high values crossfade grains seamlessly without rhythmic transients. |

---

### A2.6 Neo Learn Physical
* **Category:** Acoustic
* **Visualizer:** Modal resonant frequency bars and real-time physical excitation impulse display (X = `material`, Y = `exciter_type`).

| # | ID | Name | Range / Unit | `explainMechanism` | `explainSound` |
| :- | :--- | :--- | :--- | :--- | :--- |
| 1 | `material` | **Material** | `0% – 100%` | Shifts modal frequency ratios and high-frequency internal damping along a continuum (`Wood -> Nylon -> Glass -> Metal`). | Morphs the resonant body from warm marimba wood to nylon string, glassy crystal, and ringing bronze. |
| 2 | `exciter_type` | **Exciter Mode** | `0% – 100%` | Morphs the physical energy source from a momentary strike (`0% Mallet`), to a sharp pluck (`50% Pick`), to sustained friction/air (`100% Bow/Breath`). | Switches the instrument behavior between struck percussion, plucked strings, and sustained wind/bowed tones. |
| 3 | `exciter_force` | **Strike / Pressure** | `0% – 100%` | Controls the hardness of the mallet/pick impulse or the turbulence/velocity of the bow/breath stream. | Low values sound soft, felt-like, or breathy; high values produce sharp, bright, forceful attacks. |
| 4 | `resonator_shape` | **Geometry** | `0% – 100%` | Reconfigures the harmonic spacing of the modal filter bank (`String -> Open Tube -> Bar -> Hollow Plate`). | Alters the acoustic geometry from a 1D string or flute pipe to a 2D metallic plate or drum shell. |
| 5 | `damping` | **Damping** | `0% – 100%` | Controls the rate of energy absorption inside the resonator, especially for upper frequency modes. | High damping mutes the ring quickly like palm-muted strings; low damping lets notes ring out freely. |
| 6 | `body_size` | **Body Size** | `0% – 100%` | Shifts the acoustic formant cavity filter attached to the output of the resonator. | Scales the perceived acoustic body from a tiny thumb piano or piccolo up to a cello or concert hall marimba. |

---

### A2.7 Neo Learn Sampler
* **Category:** Sample
* **Visualizer:** Waveform of the source with live playheads, the start-offset marker and the loop region (X = `sample_offset`, Y = `timbre_tilt`).

| # | ID | Name | Range / Unit | `explainMechanism` | `explainSound` |
| :- | :--- | :--- | :--- | :--- | :--- |
| 1 | `sample_offset` | **Start Offset** | `0% – 100%` | Moves the sample playback start point forward past the initial recorded attack transient. | At 0% you hear the full natural hammer/pluck attack; raising it removes the transient for smooth, pad-like swells. |
| 2 | `timbre_tilt` | **Tone** | `-100% – +100%` | Tilts the spectrum around 1 kHz: a gentle low-pass to the left, a high shelf to the right. | Left sounds soft and dark, like a gentle touch; right sounds bright and present. |
| 3 | `source` | **Source** | `Enum (6)` | Selects the sample played across the keyboard: Dye Essence's `Choir`, `Strings`, `Bell`, `Pluck`, `Texture`, or `User`, a WAV you load. | Changes the raw material every other control shapes. |
| 4 | `loop` | **Loop** | `0% – 100%` | 0% plays the sample once from the start offset; above 0% it loops a region after the offset, crossfaded, whose length grows with the value. | One-shot sounds stay natural; looping turns any sample into a sustained pad or drone. |
| 5 | `vintage_dac` | **Vintage Grit** | `0% – 100%` | Engages 12-bit amplitude quantization, sample-rate reduction, and subtle tape wow/flutter. | Transforms pristine studio recordings into nostalgic 90s hardware sampler and lo-fi tape textures. |
| 6 | `room_space` | **Room Acoustic** | `0% – 100%` | Blends in stereo early reflections and natural studio room decay around the dry samples. | Places the instrument inside a realistic physical recording space rather than sounding dry and direct. |

---

### A2.8 Neo Learn Vector
* **Category:** Shaping
* **Visualizer:** Interactive 2D X-Y Vector Diamond with a glowing orbiting cursor showing live 4-corner mix weights (X = `vector_x`, Y = `vector_y`).

| # | ID | Name | Range / Unit | `explainMechanism` | `explainSound` |
| :- | :--- | :--- | :--- | :--- | :--- |
| 1 | `vector_x` | **Vector X (W-E)** | `-100% – +100%` | Crossfades horizontally between the West oscillator source (-100%) and the East oscillator source (+100%). | Sweeps the horizontal timbral balance between warm analog waveforms (West) and glassy digital/FM tones (East). |
| 2 | `vector_y` | **Vector Y (S-N)** | `-100% – +100%` | Crossfades vertically between the South oscillator source (-100%) and the North oscillator source (+100%). | Sweeps the vertical timbral balance between deep sub/pad foundations (South) and airy choir/noise textures (North). |
| 3 | `corner_palette` | **Corner Palette** | `Enum (4 Sets)` | Loads curated 4-oscillator combinations into the N/S/E/W corners (`Poly Classic`, `Cinema Horizon`, `Cyber Pulse`, `Organic Hybrid`). | Changes the four underlying sound sources being blended by the X-Y pad. |
| 4 | `orbit_speed` | **Orbit Rate** | `0.05 Hz – 12 Hz` | Controls the speed of the vector trajectory generator that automatically moves the cursor around `(vector_x, vector_y)`. | Determines how slowly or rhythmically the 4 sound sources morph into one another. |
| 5 | `orbit_radius` | **Orbit Depth** | `0% – 100%` | Sets the radius of the automated 2D movement around the manual X-Y center point. | At 0% the mix stays fixed where you place the cursor; at 100% it sweeps all four corners continuously. |
| 6 | `orbit_shape` | **Orbit Path** | `Enum (4 Shapes)` | Selects the geometric trajectory of the automated vector motion (`Circle`, `Figure-8`, `Diamond Step`, `Random Drift`). | Changes the rhythmic pattern and symmetry of the evolving timbral crossfades. |

---

### A2.9 Neo Learn Phase Distortion
* **Category:** Shaping
* **Visualizer:** Side-by-side plot of the piecewise phase read angle $\phi(t)$ and the resulting distorted cosine wave (X = `phase_bend`, Y = `resonance`).

| # | ID | Name | Range / Unit | `explainMechanism` | `explainSound` |
| :- | :--- | :--- | :--- | :--- | :--- |
| 1 | `phase_bend` | **Phase Bend** | `0% – 100%` | Bends the speed at which each cycle reads a cosine: 0% reads it evenly (a pure sine), 100% reads part of the cycle much faster than the rest. | Acts like a low-pass filter cutoff without using a filter—opening from a pure, round sine into a sharp, buzzy wave. |
| 2 | `resonance` | **Resonance** | `0% – 100%` | Multiplies the frequency of a hard-synced cosine window inside the single-cycle phase reset. | Produces the unmistakable, vocal, screaming 1980s digital filter sweep resonance. |
| 3 | `pd_wave_shape` | **PD Wave** | `Enum (6 Shapes)` | Selects the phase-read geometry (`Sawtooth`, `Square`, `Narrow Pulse`, `Double Sine`, `Saw-Pulse`, `Trapezoid`). | Sets the fundamental harmonic character that emerges as Phase Bend is turned up. |
| 4 | `bend_decay` | **Bend Decay** | `10 ms – 4000 ms` | Sets the decay speed of the dedicated envelope modulating `phase_bend` on each note trigger. | Creates punchy, rubbery 80s synth basses and bright digital plucks that warm up as they sustain. |
| 5 | `ring_mod` | **Ring Mod** | `0% – 100%` | Multiplies the primary phase-distorted wave with a secondary carrier oscillator. | Adds clangorous, bell-like, and harmonically complex retro digital overtones. |
| 6 | `chorus` | **Chorus** | `0% – 100%` | Engages a warm, slightly noisy bucket-brigade stereo delay chorus on the output. | Widens dry digital waveforms into lush, nostalgic 80s synth keys and pads. |

---

### A2.10 Neo Learn Wavefolder
* **Category:** Shaping
* **Visualizer:** Live nonlinear wavefolder transfer curve $y = f(x)$ and folded time-domain waveform (X = `fold_drive`, Y = `fold_symmetry`).

| # | ID | Name | Range / Unit | `explainMechanism` | `explainSound` |
| :- | :--- | :--- | :--- | :--- | :--- |
| 1 | `fold_drive` | **Wavefold** | `0% – 100%` | Amplifies the pure sine/triangle oscillator past the folding threshold so wave peaks invert back into the waveform multiple times. | Instead of clipping flat, the sound blooms with rich, metallic, and organic West Coast overtones. |
| 2 | `fold_symmetry` | **Symmetry** | `-100% – +100%` | Injects a DC bias offset prior to the wavefolder so positive and negative halves of the wave fold unequally. | Shifts the harmonic balance between hollow odd harmonics (centered) and warm, nasal even harmonics (offset). |
| 3 | `fold_stages` | **Fold Stages** | `1 – 6` | Selects how many series folding circuits the signal passes through. | Lower stages sound warm and woody; 6 stages create dense, sizzling harmonic complexity on slight envelope sweeps. |
| 4 | `linear_fm` | **Timbre FM** | `0% – 100%` | Applies phase-locked audio-rate frequency modulation to the core oscillator *before* it hits the wavefolder. | Causes the folded peaks to churn and intermodulate, producing complex percussive and aggressive textures. |
| 5 | `lpg_mode` | **Low-Pass Gate** | `0% – 100%` | Morphs the output stage from a clean VCA (`0%`) to an optical low-pass gate (`100%`) that dims brightness as volume drops. | Gives notes a natural, woody, acoustic bongo/marimba ring-out where high frequencies decay faster than bass. |
| 6 | `lpg_ping` | **Gate Strike** | `0% – 100%` | Hits the virtual optical gate with a sharp transient impulse at note-on. | Adds an organic, percussive "thwack" to the front of bass notes and West Coast plucks. |

---

### A2.11 Neo Learn Formant
* **Category:** Acoustic
* **Visualizer:** Spectral frequency response curve highlighting the 4 vocal formant peaks moving across the vowel space (X = `vowel_morph`, Y = `vocal_tract_scale`).

| # | ID | Name | Range / Unit | `explainMechanism` | `explainSound` |
| :- | :--- | :--- | :--- | :--- | :--- |
| 1 | `vowel_morph` | **Vowel (A-E-I-O-U)** | `0% – 100%` | Continuously interpolates the center frequencies of the four formant bandpass filters across `A -> E -> I -> O -> U`. | Makes the synth articulate distinct human vowel sounds as you turn the knob. |
| 2 | `vocal_tract_scale` | **Tract Size** | `-100% – +100%` | Shifts all four formant frequencies down or up in unison without changing the played musical pitch. | Changes the perceived singer from a giant deep bass (-100%) to a baritone (0%) to a high soprano/child (+100%). |
| 3 | `formant_resonance` | **Formant Q** | `0% – 100%` | Controls the bandwidth (sharpness) of the four resonant vocal tract filter peaks. | Lower values sound like warm, natural choir pads; high values sound intensely nasal, robotic, and "talking." |
| 4 | `glottal_edge` | **Glottal / Bass** | `0% – 100%` | Shapes the excitation pulse sharpness and adds sub-glottal harmonic distortion before the formant bank. | At low values the voice is soft and choral; at high values it becomes an aggressive "talking" synth bass. |
| 5 | `breath_air` | **Breath Noise** | `0% – 100%` | Mixes pitch-tracked aspirated white/pink noise into the vocal tract filters alongside the pitched pulse. | Adds whispery air, human breathiness, and wind-like choir textures. |
| 6 | `vowel_env_mod` | **Talking Env** | `-100% – +100%` | Uses the note envelope to automatically sweep `vowel_morph` on every key press. | Makes each note articulate syllables like "Wah", "Yah", or "Yoi" automatically. |

---

### A2.12 Neo Learn Spectral
* **Category:** Sample
* **Visualizer:** Real-time scrolling waterfall spectrogram with an active freeze/gate overlay (X = `spectral_shift`, Y = `spectral_blur`).

| # | ID | Name | Range / Unit | `explainMechanism` | `explainSound` |
| :- | :--- | :--- | :--- | :--- | :--- |
| 1 | `spectral_shift` | **Bin Shift / Stretch** | `-100% – +100%` | Shifts and stretches FFT frequency bin magnitudes up or down the spectrum independently of the fundamental pitch. | Creates eerie, alien harmonic structures, metallic hollowing, and sci-fi frequency transformations. |
| 2 | `spectral_blur` | **Time Smear** | `0% – 100%` | Applies temporal exponential averaging to the magnitude of each individual FFT bin across consecutive frames. | Smears sharp attacks and movement into a liquid, washed-out, infinite ambient blur. |
| 3 | `spectral_freeze` | **Spectral Freeze** | `0% – 100%` | Locks the current FFT magnitude spectrum in place while continuously advancing and randomizing bin phases. | Captures a single microscopic instant of sound and sustains it forever as a shimmering frozen drone. |
| 4 | `spectral_source` | **Exciter Source** | `Enum (6)` | Selects the raw signal fed into the FFT analyzer (`Saw`, `Choir`, `Strings`, `Bell`, `Texture`, `User`). | Determines the initial spectral DNA before frequency-domain manipulation occurs. |
| 5 | `bin_gate` | **Spectral Gate** | `0% – 100%` | Zeros out any FFT frequency bin whose magnitude falls below the set amplitude threshold. | Strips away noise and weaker harmonics, leaving only whistling, isolated sinusoidal peaks. |
| 6 | `phase_dispersal` | **Phase Scatter** | `0% – 100%` | Decorrelates phase accumulation across stereo FFT bins and smears transient phase alignment. | Turns focused, dry tones into wide, diffused, three-dimensional atmospheric clouds. |

---

### A2.13 Neo Learn String
* **Category:** Acoustic
* **Visualizer:** Animated vibrating string physics display and real-time delay-loop decay waveform (X = `pluck_position`, Y = `string_damping`).

| # | ID | Name | Range / Unit | `explainMechanism` | `explainSound` |
| :- | :--- | :--- | :--- | :--- | :--- |
| 1 | `pluck_position` | **Pick Position** | `0% – 100%` | Pre-filters the initial excitation impulse with a comb notch corresponding to where the string is plucked (`0% Bridge -> 100% Center`). | Plucking near the bridge sounds sharp, nasal, and harpsichord-like; plucking near the center sounds round and hollow. |
| 2 | `string_damping` | **String Damping** | `0% – 100%` | Adjusts the cutoff of the one-pole low-pass filter inside the delay feedback loop. | Low damping lets bright metallic harmonics ring out; high damping absorbs treble rapidly like a nylon or palm-muted guitar. |
| 3 | `pick_hardness` | **Pick Hardness** | `0% – 100%` | Controls the duration and high-frequency content of the noise/impulse burst injected into the delay line at note-on. | Morphs the initial attack from a soft flesh thumb brush to a stiff nylon plectrum or hard metal hammer. |
| 4 | `string_decay` | **Decay Time** | `0.05 s – 20 s` | Sets the loop gain per cycle from the pitch, so the string falls 60 dB in this time at any note (T60). | Controls how long the string sustains after being plucked, from tight staccato ticks to long harp rings. |
| 5 | `dispersion` | **Stiffness** | `0% – 100%` | Increases the all-pass filter coefficient inside the feedback loop so high frequencies travel faster than low frequencies. | Pushes the string physics from an ideal flexible guitar string into a stiff piano wire, chime, or metallic bell. |
| 6 | `sympathetic_res` | **Sympathetic** | `0% – 100%` | Bleeds a fraction of the plucked string's energy into a bank of unplucked octaves and fifths delay lines. | Adds the lush, resonant halo of unplayed strings vibrating sympathetically across a harp, sitar, or acoustic guitar body. |

---
