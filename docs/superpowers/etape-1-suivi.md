# Étape 1 — décisions prises et points à reprendre

Ce document résume les arbitrages faits pendant l'implémentation de l'étape 1 et les remarques de relecture volontairement reportées.

## Décisions prises en cours de route

- Branche `feature/etape-1-fondations` dans ce dépôt neuf, sans worktree séparé.
- `DashboardService` reçoit `ITenantContext` pour lire le seuil d'alerte du restaurant courant.
- `Error` et `NotFound` sont accessibles sans connexion.
- Un plat exige un prix de vente TTC et une TVA (« Indiquez le prix de vente et la TVA du plat »).
- Un slug vide (nom composé uniquement de symboles) devient `restaurant` ; les ligatures sont translittérées (œ→oe, æ→ae, ß→ss).
- `RecipeBook` ne protège pas les divisions par le rendement et la TVA : la validation est faite en amont par les services.
- Les clés étrangères sont mono-colonne : chaque service vérifie, à travers le contexte filtré, que les ids référencés (fournisseur, ingrédient, préparation) appartiennent au restaurant courant.
- Une sous-recette doit être une préparation. Une préparation utilisée ne peut pas devenir un plat.
- Tableau de bord : la moyenne ne compte que les plats au food cost > 0 ; les compteurs excluent les archivés ; un plat « incomplet » n'affiche pas de pourcentage.
- Les pages passkey du template sont supprimées (aucun schéma passkey en base).
- Les pages Identity du parcours de démo sont traduites en français ; `Account/Manage/**` reste en anglais.
- `DemoSeeder` est enregistré via `AddChefOsDemoSeeder()`, appelé uniquement par le Back (la Vitrine n'a pas Identity).
- Changement d'unité d'un ingrédient : refusé s'il est utilisé dans une recette. Sinon, le nouveau prix est obligatoire et l'historique, exprimé dans l'ancienne unité, est remplacé.

## À faire au début de l'étape 2 (avant d'ajouter `SupplierAlias` / `Receipt`)

- ✅ fait (étape 2) : test qui vérifie que chaque type `ITenantScoped` a un filtre global (`TenantModelTests`).
- ✅ fait (étape 2) : test vérifiant qu'un tenant nul ne lit rien (`TenantModelTests.NullTenant_ReadsNothing`).
- Ajouter un test `WebApplicationFactory` couvrant la chaîne claim → `ITenantContext` → filtre, ainsi que les réponses 403, 400 et 302 de `/account/active-restaurant` et le fichier d'un ticket (`/receipts/{id}/file` : 404 hors restaurant, accès refusé sans connexion).
- ✅ fait (étape 2) : scan de tickets, tickets en retard. `SetPriceAsync` ne remplace le prix courant que si le jour du ticket est égal ou postérieur à celui de la dernière entrée ; sinon le ticket n'enrichit que l'historique. L'entrée qui fixe le prix courant est toujours la plus récente par date (décalée de 1 µs si besoin), et une date de ticket postérieure à demain est refusée.

## À faire quand les invitations d'équipe arriveront

- Vérifier le rôle Owner dans `RestaurantService.UpdateSettingsAsync`. Aujourd'hui, seule la page `Settings` fait cette vérification.
- Appeler `UpdateSecurityStampAsync` quand une appartenance est retirée : le claim du restaurant actif reste valide jusqu'à la prochaine revalidation.

## Polish et robustesse (non bloquant)

- Ajouter un `ErrorBoundary` autour de `@Body` : une `DbUpdateException` due à une concurrence affiche aujourd'hui la barre d'erreur générique.
- Lors de la suppression de compte, le restaurant dont l'utilisateur était le seul propriétaire reste en base. Prévoir un avertissement ou une suppression.
- Ajouter une entrée « Nouveau restaurant » dans le sélecteur (`/onboarding` fonctionne déjà).
- Concurrence : création ou renommage simultané d'un même slug, suppression puis référence, cycles, onboarding sans JavaScript. Ces cas ne sont pas atomiques ; c'est acceptable tant qu'il n'y a qu'un seul utilisateur.
- Graphique des prix : il est plat sous 1 € (pas d'axe entier imposé par MudChart) et les dates sont en heure serveur. Envisager un petit tableau d'historique.
- Catégories d'ingrédients : `Distinct` est sensible à la casse et charge tous les ingrédients.
- Éditeur de recettes (513 lignes) : extraire la ligne et le panneau récapitulatif ; `RecipeService.ListAsync` n'est plus utilisé par l'UI.
- Le libellé « Unité incompatible — renseignez le poids moyen d'une pièce » est inadapté pour un écart g/ml.
- Le coût par unité d'une préparation incomplète n'affiche pas la mention « partiel ».
- Accessibilité : les autocomplétions de ligne n'ont pas de nom accessible, et certaines infobulles sont en double.
- Le test de culture des `MudNumericField` ne vérifie que la sous-chaîne `Culture=`.
- `DemoSeederTests` compare les noms avec un ordre dépendant de la culture : passer à l'ordinal.
- Au premier démarrage sur une base vide, EF journalise une ligne `fail:` sur `__EFMigrationsHistory`, sans conséquence.
- Restent en anglais : `Account/Manage/**`, la 2FA et les connexions externes.
