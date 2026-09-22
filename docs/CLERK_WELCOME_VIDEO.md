# Clerk welcome clip

The clerk welcome screen (shown once a day, right after a clerk signs in, before the dashboard) plays a 5-second clip behind the greeting and a **Clock in** button.
It looks for two files. Until they exist it shows a green gradient, so nothing breaks.

| File | What |
|---|---|
| `client/inventory-ui/public/clerk-welcome.mp4` | the 5-second clip, H.264, muted, no audio needed, ideally under 3 MB |
| `client/inventory-ui/public/clerk-welcome.jpg` | one clean still frame from the clip (used as the poster and as the fallback when video can't play, and for people who ask their device for reduced motion) |

Rebuild the UI after adding them (`npx ng build`), or just copy them into the deployed `browser/` folder next to `index.html`.

## Prompt for Higgsfield (image-to-video, 5 seconds)

Use your photo of the man in the olive-and-gold patterned shirt at the desk, between the purple Chewy Pets bag and the orange Puppy bag.

> Locked-off camera, no zoom. The man stays seated in the grey office chair and slowly swivels the chair a few degrees left, then right, with a relaxed, friendly expression, as if rolling gently. On the desk in front of him a clean white plate is in the foreground; a steady stream of dry dog-food kibble pours from the top of the frame into the plate, piling up naturally. Everything else stays still: the two dog-food bags, the wall banner, the laptop. Soft office lighting, photorealistic, no text, no logos changing, no extra people, smooth natural motion, 5 seconds.

Tips: keep the camera static so the clip can sit behind text; ask for a **16:9 or 4:3** output (the page crops to fill the screen); if the kibble looks odd, regenerate rather than editing. Extract the poster frame from the first second, where the plate is still empty.

Note: the current photo has the desk clutter (socket, cables) at the edges and a slightly uneven wall. Cropping the still to the centre 80% before generating gives a cleaner result.
