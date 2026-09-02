# TCO

**TERA Classic+ Optimizer**

TCO improves TERA graphics and helps the game run more smoothly.

## Before You Start

- Close TERA and its launcher.
- Know where your TERA folder is.
- Start TCO.

## Quick Setup

1. Click **Browse**.
2. Select your main TERA folder.
3. Choose an **Engine configuration**.
4. Enable **PC Only** if you do not use a controller.
5. Enable **Patch TCC and Shinra** only if you use them.
6. Click **Apply complete pipeline**.
7. Wait for **Completed successfully**.

Your TERA folder is the folder that contains both **Binaries** and **S1Game**.

Example:

```text
S:\TERA
```

Do not select the **Binaries** folder itself.

## Engine Choices

### TCO Standard

Best image quality. Recommended for most players.

### TCO No-Dyn Light

Almost the same image quality, but moving lights are disabled. Use this for better and more stable FPS.

### PC Only

Disables controller input. Enable it when you play only with keyboard and mouse.

### Patch TCC and Shinra

Applies the included TCC and Shinra setup. Enable it only when TCC and Shinra are already installed.

## Main Buttons

### Apply complete pipeline

Applies your chosen game settings and activates ReShade and DXVK. It also patches TCC and Shinra when that option is enabled.

### Scan current configuration

Checks your current setup. It does not change the game.

The scan shows if these parts are installed and active:

- Engine settings
- ReShade
- DXVK
- TCC
- Shinra

Click **Open report** to read the saved result.

## Individual Controls

### Engine settings

- **Apply** uses the selected TCO engine choice.
- **Restore** returns the game settings saved before the first TCO setup.

### ReShade

ReShade improves colors, sharpness, and screen effects.

- **Activate** turns ReShade on.
- **Deactivate** turns ReShade off.

### DXVK

DXVK can make the game smoother and improve FPS on many computers.

- **Activate** turns DXVK on.
- **Deactivate** turns DXVK off.

## If Something Looks Wrong

1. Close TERA.
2. Open TCO.
3. Click **Scan current configuration**.
4. Deactivate ReShade and test the game.
5. If the problem remains, deactivate DXVK and test again.
6. Use **Restore** beside Engine settings if needed.

Use **Open log** after an error to see more details.
