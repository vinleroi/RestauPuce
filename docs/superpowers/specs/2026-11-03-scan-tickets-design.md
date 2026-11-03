# ChefOS — Étape 2 : Scan des tickets fournisseurs

**Date** : 2026-11-03
**Statut** : design rédigé par Claude. L'utilisateur a demandé de passer directement au code, donc **tous les choix ci-dessous sont des choix de Claude**. Les tests se feront avec un ticket de test fourni par l'utilisateur.
**Base** : branche `feature/etape-2-tickets`, créée depuis `feature/etape-1-fondations`.

## 1. Intention

Le chef prend son ticket ou sa facture fournisseur en photo (ou dépose un PDF). L'IA lit les produits, les quantités et les montants. Le chef vérifie et associe chaque ligne à un ingrédient, puis valide. Les prix des ingrédients, leur historique et donc le coût de toutes les recettes se mettent alors à jour. La correspondance entre une ligne de ticket et un ingrédient est mémorisée par fournisseur : au ticket suivant, l'association est automatique.

**Anti-objectifs**
- Aucun prix n'est modifié sans validation humaine.
- On ne présente jamais un prix calculé comme sûr quand une quantité ou une unité manque.
- Un restaurant ne voit jamais les tickets d'un autre.

## 2. Parcours

1. `/receipts` → « Scanner un ticket ».
2. `/receipts/new` : envoi d'une photo (JPEG, PNG ou WebP, avec la caméra sur mobile) ou d'un PDF, 10 Mo maximum. Le fichier est stocké, puis lu par l'IA (indicateur de progression).
3. `/receipts/{id}` : écran de validation, avec l'image du ticket à côté du tableau des lignes.
   - En-tête : fournisseur (choisi dans la liste ou créé à partir du nom lu), date, n° de facture, total.
   - Lignes : libellé lu, quantité, unité, total HT (modifiable), ingrédient (autocomplétion, pré-rempli), prix calculé dans l'unité d'affichage (€/kg, €/l, €/pièce), ancien prix, variation en %, case « Ignorer ». On peut ajouter une ligne à la main.
   - « Enregistrer » sauvegarde le brouillon. « Valider et mettre à jour les prix » applique le ticket.
4. Après validation, un récapitulatif liste chaque ingrédient mis à jour avec son ancien prix, son nouveau prix et la variation. Le ticket passe au statut « Appliqué », en lecture seule.
5. Si la lecture échoue (clé API absente, erreur réseau, image illisible), le ticket passe au statut « Échec ». Le message est affiché et le chef peut saisir les lignes à la main.

## 3. Règles métier

- **Prix HT.** Les coûts de revient sont HT, le restaurant récupérant la TVA. Si le ticket affiche des montants TTC et que le taux de TVA de la ligne est connu, le total HT vaut `TTC ÷ (1 + taux)`. Si le taux est inconnu, la ligne est marquée « TVA à vérifier » et le total reste modifiable.
- **Prix par unité de base.** Le total HT de la ligne est divisé par la quantité convertie dans l'unité de base de l'ingrédient, avec `UnitConverter.ToBase` (le poids moyen d'une pièce est pris en compte). Les conditionnements sont multipliés à la lecture (« 6 × 1 L » donne 6 l).
- **Problèmes bloquants pour une ligne non ignorée** : pas d'ingrédient, quantité ou unité manquante, unité incompatible, total ≤ 0.
- **Validation.** Elle est refusée tant qu'un problème bloquant subsiste. Chaque ligne appliquée appelle `IngredientService.SetPriceAsync(..., PriceSource.Receipt, fournisseur, date du ticket)`. Si plusieurs lignes visent le même ingrédient, le prix retenu est la moyenne pondérée : somme des totaux HT ÷ somme des quantités de base.
- **Tickets en retard.** Un ticket daté d'avant le dernier prix connu d'un ingrédient ajoute une entrée à l'historique, mais ne remplace pas le prix courant.
- **Mémorisation.** À la validation, chaque couple (fournisseur, libellé normalisé) est associé à l'ingrédient choisi dans `SupplierAlias`, en créant ou en mettant à jour l'association.
- **Suggestion d'ingrédient**, dans cet ordre :
  1. l'association mémorisée pour le fournisseur ;
  2. une association sans fournisseur ;
  3. le nom d'ingrédient le plus proche (recouvrement de mots normalisés ≥ 0,5) ;
  4. sinon, aucune suggestion.
  Les ingrédients archivés ne sont jamais suggérés.
- **Fournisseur.** Il est rapproché par nom normalisé (sans accents, sans casse ni ponctuation). S'il n'existe pas, le chef peut le créer en un clic depuis l'en-tête.

## 4. Lecture par IA (choix de Claude)

- Le SDK officiel `Anthropic` pour C# est utilisé avec le modèle `claude-opus-5-5`, l'effort `medium` et une sortie structurée (`output_config.format`, schéma JSON). Le fallback serveur en cas de refus est activé par défaut (`fallbacks: "default"`).
- L'image est envoyée en base64 ; un PDF passe par un bloc `document`.
- La clé API est lue dans la configuration `Anthropic:ApiKey` (user-secrets en développement), sinon dans la variable d'environnement `ANTHROPIC_API_KEY`. Elle n'est jamais écrite dans `appsettings*.json`.
- `Receipts:Reader` vaut `Claude` par défaut, ou `Demo`. Le mode Demo renvoie un ticket Metro factice dont les libellés correspondent aux ingrédients du Petit Bistro, pour montrer le parcours sans clé ni coût.
- Le schéma extrait contient :
  - le fournisseur, la date, le n° de facture, si les montants sont TTC, et le total ;
  - pour chaque ligne : le libellé tel qu'imprimé, la quantité, l'unité (`g | kg | ml | cl | l | piece`, ou null), le prix unitaire, le total de la ligne et le taux de TVA.

## 5. Données (nouvelles entités, toutes `ITenantScoped`)

- `Receipt` : `SupplierId?`, `SupplierNameRaw`, `ReceiptDate?`, `InvoiceNumber?`, `AmountsIncludeVat`, `Total?`, `Status` (`Pending | Extracted | Failed | Applied`), `ErrorMessage?`, `CreatedAt`, `AppliedAt?`, `Lines`.
- `ReceiptFile` : `ReceiptId` (1-1), `ContentType`, `FileName`, `Data` (bytea), stocké à part pour ne pas charger les octets avec les listes.
- `ReceiptLine` : `ReceiptId`, `Position`, `RawLabel`, `Quantity?`, `Unit?` (`MeasureUnit`), `UnitPrice?`, `LineTotal?` (tel qu'imprimé), `VatRate?`, `LineTotalExclVat?`, `IngredientId?`, `IsIgnored`, `AppliedPricePerBaseUnit?`.
- `SupplierAlias` : `SupplierId?`, `NormalizedLabel`, `IngredientId`. Index unique sur (`RestaurantId`, `SupplierId`, `NormalizedLabel`), avec les NULL non distincts.

## 6. Hors périmètre

Import Peppol / UBL (piste forte pour plus tard, puisque la facturation électronique B2B est obligatoire en Belgique depuis 2026), saisie des ventes et statistiques (étape 3), gestion des stocks, OCR hors ligne.

## 7. Terminé quand

- En mode `Demo`, le parcours complet (scanner, valider, voir les prix et les coûts de recettes changer) fonctionne avec le compte de démo.
- En mode `Claude` avec une clé, un vrai ticket photographié est lu et pré-rempli.
- `dotnet test` passe et le build ne produit aucun avertissement.
