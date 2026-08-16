### Installation
1. Install [CounterStrikeSharp](https://github.com/roflmuffin/CounterStrikeSharp/releases) and [Metamod](https://www.sourcemm.net/downloads.php/?branch=master)
2. Download [RecordsData](https://github.com/Local-KZ/RecordsData/releases)
3. Unzip the archive and upload it into `game/csgo`
4. Configuration path `game/csgo/cfg/plugins/RecordsData/config.json`

### ⚙️ Configuration

```json
{
  "database_path": "csgo/addons/cs2kz/data/cs2kz.sqlite3", // default database path
  "github_repo": "yourname/reponame",  // like example https://github.com/yourname/reponame
  "github_token": "github_pat_*****",  // access token to repository
  "records_path": "data/records.json", // default records path
  "players_path": "data/players.json"  // default players path
}
```