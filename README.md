# Pre-release SVSim Client Loader
Pre-release version of the SVSim Client Loader, for dumping user data. This readme will document the steps you need to go through to dump your current user data. 

## What will be saved
Currency, cosmetics, cards, class xp/levels, decks, missions, achievements, story clears
## What will be lost
Probably everything else

## Instructions

* Download BepinEx [here](https://github.com/BepInEx/BepInEx/releases/download/v5.4.23.5/BepInEx_win_x86_5.4.23.5.zip)
* Follow the installation instructions (should be just extracting it to your Shadowverse game folder)
* Download the latest version of the client loader from releases [here](https://github.com/SVSimOrg/SVSimClientLoader/releases/tag/prerelease)
* Extract the contained BepinEx folder into your shadowverse folder (overwriting the existing BepinEx folder)
  * This will set up the client loader with a default configuration to dump your user data
* Start the game
* Once on the home screen, you have all your main data dumped
  * If you want Mission status as well, goto the missions menu
  * If you want story clears as well, go into each story's chapter selection screen (yes all of them)
* You will see a folder inside your BepinEx folder called svsim-captures. Inside that you will see a folder with today's date and some other stuff. Go into that, and you'll see your userdata and story progress as export files. Keep those somewhere.
* Go into the bepinex config folder, open the SVSimLoader.cfg and turn off DumpUserData (change 'true' to 'false') unless you want a slowly accumulating log of nonsense. Or just delete the plugin until the final version is released.

## Will I get banned?
Probably not, but on the bright side, it would only last about a week!
