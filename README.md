# Pre-release SVSim Client Loader
Pre-release version of the SVSim Client Loader, for dumping user data. This readme will document the steps you need to go through to dump your current user data. 

## What will be saved
Currency, cosmetics, cards, class xp/levels, decks, missions, achievements, story clears
## What will be lost
Probably everything else

## Instructions

* Download BepinEx [here](https://github.com/BepInEx/BepInEx/releases/download/v5.4.23.5/BepInEx_win_x86_5.4.23.5.zip)
* Follow the installation instructions (should be just extracting it to your SV game folder)
* Download the latest version of the client loader from releases [here](https://github.com/SVSimOrg/SVSimClientLoader/releases/tag/prerelease)
* Extract the contained BepinEx folder into your SV folder (overwriting the existing BepinEx folder)
  * This will set up the client loader with a default configuration to dump your user data
* Start the game
* Once on the home screen, you have all your main data dumped
  * If you want Mission status as well, goto the missions menu
  * If you want story clears as well, go into each story's chapter selection screen (yes all of them)
* You will see a folder inside your BepinEx folder called svsim-captures. Inside that you will see a folder with today's date and some other stuff. Go into that, and you'll see your userdata and story progress as export files. Keep those somewhere.
* Go into the bepinex config folder, open the SVSimLoader.cfg and turn off DumpUserData (change 'true' to 'false') unless you want a slowly accumulating log of nonsense. Or just delete the plugin until the final version is released.

## Will I get banned?
Probably not, but on the bright side, it would only last about a week!

## Troubleshooting

If you are not seeing your user data being exported, there are (probably) a number of reasons as to why this may be happening. This will take you through some of the steps for troubleshooting the issue:
* Make sure SVSimLoader.cfg has DumpUserData set to 'true'.
* Check to make sure you're viewing the right folder. A new folder in svsim-captures will be made every game launch, and all exported data for a session will go into the latest. This is an artifact of the loader initially being just for capturing traffic logs.
* If you've checked that and still cannot see any output, then go into BepInEx\config\BepInEx.cfg, search for [Logging.Console], look for "Enabled = false" in that section (underneath it, before the next bracketed item) and replace "false" with "true". You'll know this worked if you start your game and see a black command prompt show up alongside the game
* Once this is happening, check the command prompt window while you enter the home screen. If you see red text (errors) start popping up, something is going wrong (with the export or otherwise). If you are technically savvy and can fix the issue, great. If not, jump down to "Enabling Capture Logs", follow those steps, and then restart the game. Copy the console output once you see the red text, and send that along with the generated session logs over for inspection.
* If you do not see an error, and you've done all the other steps, then that's very interesting! Follow the "Enabling Capture Logs" section, and then send over the console output along with your capture logs for the session for inspection.

## How do I contribute?
I don't want to spend my free time reviewing pull requests right now, so the best way you can contribute is by turning on the capture logs and sending them over. Follow "Enabling Capture Logs" below.

## Enabling Capture Logs
* Open SVSimLoader.cfg (in BepInEx\config).
* Locate EnableTrafficCapture and EnableBattleCapture
* Make sure both values are set to "true" instead of false
* Whenever you boot the game, your svsim-captures folder for the session should have a traffic.ndjson file in it.
* Whenever you enter a battle (or join a lobby, it's a bit of a misnomer) you will also see battle-traffic.ndjson appear.
* Whenever you feel like it, go ahead and zip up your folder or folders and send them over for use in developing the emulator. Live data captures always help with figuring out how things work, or with scraping server-only data like achievements, that have no client master list.
* Whenever you're done or want to stop logging, set the capture values back to false

### Is there any sensitive data in the captures?
Possibly. The captures capture all API and websocket traffic I could think to hook into. While there isn't anything like your steam credentials, your steam id does get sent across, which can identify your steam account. If you chat with friends in SV or join lobbies, thats all api/websocket traffic getting captures. Your friends names get captured when you view friends, your date of birth I think comes back on the index load, etc.

If this is concerning, I highly recommend not turning this on. If you have a technical friend you trust to scrub out sensitive stuff, great. I'm not interested in reading other people's messages or SV information, but just keep your own opsec in mind based on what you care about.

### Why isn't this on by default?
There is no log rotation. If you enable captures, you'll just start having log files pile up. So I'd rather people go out of their way to enable it and know how to disable it.
