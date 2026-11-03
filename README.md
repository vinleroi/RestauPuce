# ChefOS

SaaS multi-restaurants qui aide les chefs à connaître le vrai coût de leurs plats et ce qu'ils gagnent ou perdent.

Le produit comprend deux sites qui partagent une même base PostgreSQL :

- **ChefOS.Back** : l'outil du chef. Ingrédients (rendement, historique des prix), fournisseurs, recettes et sous-préparations, coût de revient et food cost recalculés en direct, tableau de bord, réglages du restaurant.
- **ChefOS.Vitrine** : le site public de chaque restaurant. Pour l'instant un squelette (« bientôt disponible »), branché sur la base.

Cette première étape pose les fondations et le back du chef. La conception est dans [`docs/superpowers/specs`](docs/superpowers/specs/2026-11-02-fondations-back-chef-design.md), le plan d'implémentation dans [`docs/superpowers/plans`](docs/superpowers/plans/2026-11-02-fondations-back-chef.md).

## Prérequis

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [Docker](https://www.docker.com/) (la base de développement et les tests en dépendent)

## Démarrer le back

```bash
# 1. Base PostgreSQL de développement (127.0.0.1:5433)
docker compose up -d

# 2. L'outil du chef
dotnet run --project src/ChefOS.Back
```

Le back écoute sur <http://localhost:5268>. En environnement `Development`, il applique les migrations au démarrage puis crée, dans une base vide, le restaurant de démonstration « Le Petit Bistro » (fournisseurs, ingrédients avec historique de prix, préparations, plats dont certains sont à surveiller).

### Compte de démonstration

| E-mail | Mot de passe |
|--------|--------------|
| `demo@chefos.local` | `ChefDemo2026!` |

Ces identifiants sont des **valeurs de développement uniquement** : ils viennent de la section `Demo` de `src/ChefOS.Back/appsettings.Development.json` et ne servent qu'au seed de la base locale. Le seed ne s'exécute qu'en `Development` ; ne reprenez jamais ces valeurs ailleurs.

Vous pouvez aussi créer votre propre compte (`/Account/Register`) : un assistant vous fait ensuite créer votre restaurant. Deux comptes ne voient jamais les données l'un de l'autre.

### Repartir d'une base vierge

```bash
docker compose down -v && docker compose up -d
```

Le volume est supprimé : le prochain lancement du back recrée le schéma et les données de démonstration.

## Démarrer la vitrine

```bash
dotnet run --project src/ChefOS.Vitrine
```

La vitrine écoute sur <http://localhost:5108> et partage la base du back (même chaîne de connexion de développement).

## Scan des tickets

Le chef téléverse la photo (JPEG, PNG, WebP) ou le PDF d'un ticket ou d'une facture de fournisseur ; l'IA en lit les lignes, le chef corrige le brouillon, puis applique les nouveaux prix aux ingrédients. La lecture se fait avec [Claude](https://www.anthropic.com/claude) (modèle `claude-opus-5-5`, réponse JSON imposée par un schéma). Coût indicatif : **quelques centimes par ticket**.

### Avec Claude

Il faut une clé API Anthropic ([console Anthropic](https://console.anthropic.com/)). Rangez-la dans les secrets utilisateur du back, jamais dans un fichier `appsettings` ni dans git :

```bash
dotnet user-secrets set Anthropic:ApiKey <clé> --project src/ChefOS.Back
```

Autre possibilité : la variable d'environnement `ANTHROPIC_API_KEY` (ou `Anthropic__ApiKey`), qui sert aussi en production. La clé de `Anthropic:ApiKey` l'emporte sur la variable `ANTHROPIC_API_KEY`.

Sans clé, le back démarre quand même : chaque ticket téléversé passe en « échec » avec le message « Clé API Anthropic non configurée », et le chef peut le saisir à la main.

### Démonstration sans clé

`appsettings.Development.json` choisit le lecteur avec `"Receipts": { "Reader": "Claude" }`. Pour une démonstration sans clé ni réseau, passez-le à `Demo` : quel que soit le fichier téléversé, le lecteur renvoie après une courte pause un ticket Metro daté du jour, dont les libellés correspondent aux ingrédients du Petit Bistro (dont un beurre à +10 %) et une ligne sans correspondance (« SACS POUBELLE 100L »).

```bash
Receipts__Reader=Demo dotnet run --project src/ChefOS.Back
```

Le choix du lecteur et la clé sont lus au démarrage : redémarrez le back après les avoir modifiés. Aucun test automatisé n'appelle l'API Anthropic.

## Tests

```bash
dotnet test
```

**Docker doit tourner** : les tests d'infrastructure démarrent un conteneur PostgreSQL jetable (Testcontainers) et ne touchent pas à la base de développement.

## Migrations

Le schéma est géré par EF Core dans `ChefOS.Infrastructure`. Pour créer une migration après une modification du modèle :

```bash
dotnet tool restore
dotnet ef migrations add NomDeLaMigration -p src/ChefOS.Infrastructure -o Data/Migrations
```

En développement, le back applique les migrations en attente à chaque démarrage. Pour les appliquer à la main : `dotnet ef database update -p src/ChefOS.Infrastructure`. L'outil `dotnet ef` utilise la base locale du `docker-compose.yml`.

## Structure

```
src/
  ChefOS.Core/            Règles métier pures : unités, coût de revient, food cost, TVA (sans dépendance)
  ChefOS.Infrastructure/  EF Core + PostgreSQL, Identity, services, isolation par restaurant, données de démo
  ChefOS.Back/            L'outil du chef (Blazor Server + MudBlazor)
  ChefOS.Vitrine/         Le site public de chaque restaurant
tests/ChefOS.Tests/       Tests unitaires et d'infrastructure (PostgreSQL via Testcontainers)
```

## Conventions

- Interface en français, culture `fr-BE` : montants « 12,60 € », saisie décimale avec virgule.
- Les taux (TVA, seuil d'alerte) sont stockés en fraction (`0.12`) et affichés en pourcentage (« 12 % »).
- Chaque donnée métier appartient à un restaurant ; l'isolation est garantie par un filtre global du contexte EF et par un contrôle à l'écriture.
- Hors `Development`, la chaîne de connexion `ConnectionStrings:ChefOs` doit être fournie (par exemple via la variable d'environnement `ConnectionStrings__ChefOs`) ; ni migrations automatiques ni données de démo.
