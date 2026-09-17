using System.Text.RegularExpressions;

namespace FinSight.Core.Categories;

/// <param name="Pattern">Case-insensitive regex matched against the cleaned description.</param>
/// <param name="Merchant">Canonical display name; null for keyword rules that only imply a category.</param>
public sealed record CatalogEntry(string Pattern, string? Merchant, string CategoryId, double Confidence = 0.95)
{
    internal Regex Regex { get; } = new(Pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
}

/// <summary>
/// Well-known merchants across Canada, the US, the UK and Europe, plus generic keywords. This is a
/// starting point, not an exhaustive list: anything it misses falls through to AI categorization
/// and then to per-user merchant rules, which take precedence over everything here.
/// </summary>
public static class MerchantCatalog
{
    public static readonly IReadOnlyList<CatalogEntry> Merchants =
    [
        // Subscriptions and streaming
        new(@"\bNETFLIX", "Netflix", CategoryTaxonomy.Subscriptions, 0.99),
        new(@"\bSPOTIFY", "Spotify", CategoryTaxonomy.Subscriptions, 0.99),
        new(@"\bDISNEY ?(PLUS|\+)", "Disney+", CategoryTaxonomy.Subscriptions, 0.99),
        new(@"\b(PRIME VIDEO|AMAZON PRIME|PRIMEVIDEO|AMZN PRIME)", "Amazon Prime", CategoryTaxonomy.Subscriptions, 0.97),
        new(@"\bYOUTUBE ?(PREMIUM|MUSIC|TV)?\b|GOOGLE \*YOUTUBE", "YouTube", CategoryTaxonomy.Subscriptions, 0.95),
        new(@"\bAPPLE\.COM/BILL|\bAPPLE\.COM BILL|\bITUNES", "Apple Services", CategoryTaxonomy.Subscriptions, 0.93),
        new(@"\bHULU\b", "Hulu", CategoryTaxonomy.Subscriptions, 0.99),
        new(@"\b(MAX\.COM|HBO ?MAX)\b", "Max", CategoryTaxonomy.Subscriptions, 0.98),
        new(@"\bCRAVE\b", "Crave", CategoryTaxonomy.Subscriptions, 0.97),
        new(@"\bPARAMOUNT\+?", "Paramount+", CategoryTaxonomy.Subscriptions, 0.95),
        new(@"\bAUDIBLE\b", "Audible", CategoryTaxonomy.Subscriptions, 0.97),
        new(@"\bOPENAI|CHATGPT", "OpenAI", CategoryTaxonomy.Subscriptions, 0.95),
        new(@"\bANTHROPIC|CLAUDE\.AI", "Anthropic", CategoryTaxonomy.Subscriptions, 0.95),
        new(@"\bADOBE\b", "Adobe", CategoryTaxonomy.Subscriptions, 0.95),
        new(@"\bDROPBOX\b", "Dropbox", CategoryTaxonomy.Subscriptions, 0.97),
        new(@"\bGOOGLE \*?(STORAGE|ONE|GSUITE|WORKSPACE)", "Google One", CategoryTaxonomy.Subscriptions, 0.95),
        new(@"\bMICROSOFT ?\*?(365|OFFICE)", "Microsoft 365", CategoryTaxonomy.Subscriptions, 0.95),
        new(@"\bPATREON\b", "Patreon", CategoryTaxonomy.Subscriptions, 0.95),
        new(@"\bGITHUB\b", "GitHub", CategoryTaxonomy.Subscriptions, 0.93),
        new(@"\bNOTION\b", "Notion", CategoryTaxonomy.Subscriptions, 0.9),
        new(@"\bCANVA\b", "Canva", CategoryTaxonomy.Subscriptions, 0.93),
        new(@"\bFIGMA\b", "Figma", CategoryTaxonomy.Subscriptions, 0.95),
        new(@"\bZOOM\.US|\bZOOM VIDEO", "Zoom", CategoryTaxonomy.Subscriptions, 0.93),
        new(@"\b1PASSWORD|\bLASTPASS|\bBITWARDEN|\bNORDVPN|\bEXPRESSVPN|\bPROTON ?(MAIL|AG|VPN)", null, CategoryTaxonomy.Subscriptions, 0.93),
        new(@"\bSIRIUS ?XM|\bCRUNCHYROLL|\bDAZN\b|\bBRITBOX|\bMUBI\b|\bKINDLE UNLIMITED|\bSCRIBD", null, CategoryTaxonomy.Subscriptions, 0.93),

        // Food delivery (before ride sharing so "UBER EATS" wins over "UBER")
        new(@"\bUBER ?\*? ?EATS|UBEREATS", "Uber Eats", "food.delivery", 0.98),
        new(@"\bDOORDASH|\bDD \*DOORDASH", "DoorDash", "food.delivery", 0.98),
        new(@"\bSKIP ?THE ?DISHES|SKIPTHEDISHES", "SkipTheDishes", "food.delivery", 0.98),
        new(@"\bGRUBHUB", "Grubhub", "food.delivery", 0.98),
        new(@"\bDELIVEROO", "Deliveroo", "food.delivery", 0.98),
        new(@"\bJUST ?EAT", "Just Eat", "food.delivery", 0.95),
        new(@"\bINSTACART", "Instacart", "food.groceries", 0.9),

        // Ride sharing and transit
        new(@"\bUBER\b|\bUBER ?\*? ?TRIP", "Uber", "transportation.ride-sharing", 0.93),
        new(@"\bLYFT\b", "Lyft", "transportation.ride-sharing", 0.97),
        new(@"\bPRESTO\b", "PRESTO", "transportation.public-transit", 0.97),
        new(@"\bTTC\b", "TTC", "transportation.public-transit", 0.95),
        new(@"\bGO ?TRANSIT|METROLINX", "GO Transit", "transportation.public-transit", 0.95),
        new(@"\bCOMPASS ?(CARD|VENDING)|TRANSLINK", "TransLink", "transportation.public-transit", 0.95),
        new(@"\bSTM\b|\bOPUS\b", "STM", "transportation.public-transit", 0.85),
        new(@"\bMTA\b|\bOMNY\b|\bMETROCARD", "MTA", "transportation.public-transit", 0.95),
        new(@"\bTFL\b|TRANSPORT FOR LONDON", "TfL", "transportation.public-transit", 0.97),
        new(@"\bVIA ?RAIL|\bAMTRAK", "Rail", "travel.other", 0.9),

        // Fuel, parking, car
        new(@"\bSHELL\b", "Shell", "transportation.fuel", 0.9),
        new(@"\bESSO\b", "Esso", "transportation.fuel", 0.95),
        new(@"\bPETRO[- ]?CAN", "Petro-Canada", "transportation.fuel", 0.95),
        new(@"\bCHEVRON\b", "Chevron", "transportation.fuel", 0.95),
        new(@"\bEXXON|\bMOBIL\b", "ExxonMobil", "transportation.fuel", 0.93),
        new(@"\bPIONEER\b|\bULTRAMAR|\bHUSKY\b|\bCIRCLE K", null, "transportation.fuel", 0.8),
        new(@"\bIMPARK|\bGREEN ?P\b|\bHONK ?MOBILE|\bPARKMOBILE|\bPAYBYPHONE|\bINDIGO PARK|\bSPOTHERO", null, "transportation.parking", 0.93),
        new(@"\b407 ?ETR\b|\bE-?ZPASS", null, "transportation.car", 0.9),
        new(@"\bCANADIAN TIRE", "Canadian Tire", "shopping.general", 0.7),

        // Groceries
        new(@"\bCOSTCO", "Costco", "food.groceries", 0.85),
        new(@"\bLOBLAW", "Loblaws", "food.groceries", 0.95),
        new(@"\bNO ?FRILLS", "No Frills", "food.groceries", 0.97),
        new(@"\bREAL CANADIAN SUPERSTORE|\bSUPERSTORE", "Real Canadian Superstore", "food.groceries", 0.95),
        new(@"\bSOBEYS", "Sobeys", "food.groceries", 0.97),
        new(@"\bMETRO ?(INC|ONTARIO|PLUS)?\b(?! ?(TRANSIT|BANK|LINX))", "Metro", "food.groceries", 0.75),
        new(@"\bFOOD ?BASICS", "Food Basics", "food.groceries", 0.97),
        new(@"\bFRESHCO", "FreshCo", "food.groceries", 0.97),
        new(@"\bFARM ?BOY", "Farm Boy", "food.groceries", 0.97),
        new(@"\bLONGO'?S", "Longo's", "food.groceries", 0.97),
        new(@"\bT ?& ?T SUPERMARKET", "T&T Supermarket", "food.groceries", 0.97),
        new(@"\bSAFEWAY", "Safeway", "food.groceries", 0.97),
        new(@"\bWHOLE ?FOODS|\bWFM\b", "Whole Foods", "food.groceries", 0.97),
        new(@"\bTRADER JOE", "Trader Joe's", "food.groceries", 0.97),
        new(@"\bKROGER", "Kroger", "food.groceries", 0.97),
        new(@"\bPUBLIX", "Publix", "food.groceries", 0.97),
        new(@"\bALDI\b", "Aldi", "food.groceries", 0.97),
        new(@"\bLIDL\b", "Lidl", "food.groceries", 0.97),
        new(@"\bTESCO", "Tesco", "food.groceries", 0.95),
        new(@"\bSAINSBURY", "Sainsbury's", "food.groceries", 0.97),
        new(@"\bWAITROSE", "Waitrose", "food.groceries", 0.97),
        new(@"\bASDA\b", "Asda", "food.groceries", 0.97),
        new(@"\bCARREFOUR", "Carrefour", "food.groceries", 0.95),

        // Coffee
        new(@"\bSTARBUCKS", "Starbucks", "food.coffee", 0.98),
        new(@"\bTIM ?HORTON", "Tim Hortons", "food.coffee", 0.95),
        new(@"\bSECOND CUP", "Second Cup", "food.coffee", 0.97),
        new(@"\bDUNKIN", "Dunkin'", "food.coffee", 0.95),
        new(@"\bBALZAC'?S|\bPILOT COFFEE|\bBLUE BOTTLE|\bPEET'?S|\bCOSTA COFFEE|\bPRET A MANGER", null, "food.coffee", 0.9),

        // Restaurants
        new(@"\bMCDONALD", "McDonald's", "food.restaurants", 0.97),
        new(@"\bA ?& ?W\b", "A&W", "food.restaurants", 0.93),
        new(@"\bSUBWAY\b", "Subway", "food.restaurants", 0.9),
        new(@"\bCHIPOTLE", "Chipotle", "food.restaurants", 0.97),
        new(@"\bPIZZA ?PIZZA|\bPIZZA HUT|\bDOMINO'?S|\bPAPA JOHN", null, "food.restaurants", 0.93),
        new(@"\bWENDY'?S\b|\bBURGER KING|\bKFC\b|\bTACO BELL|\bPOPEYES|\bFIVE GUYS|\bHARVEY'?S|\bMARY BROWN|\bNANDO'?S|\bCHICK-?FIL-?A|\bSHAKE SHACK|\bPANERA|\bOSMOW'?S|\bBARBURRITO|\bFRESHII|\bSWEETGREEN", null, "food.restaurants", 0.93),

        // Shopping
        new(@"\bAMZN ?MKTP|\bAMAZON\.(CA|COM|CO\.UK|DE)|\bAMZN\.COM|\bAMAZON MARKETPLACE|\bAMAZON\b", "Amazon", "shopping.general", 0.85),
        new(@"\bWAL-?MART", "Walmart", "shopping.general", 0.8),
        new(@"\bTARGET\b", "Target", "shopping.general", 0.8),
        new(@"\bDOLLARAMA", "Dollarama", "shopping.general", 0.9),
        new(@"\bIKEA\b", "IKEA", "housing.home", 0.9),
        new(@"\bHOME ?DEPOT", "The Home Depot", "housing.home", 0.93),
        new(@"\bLOWE'?S\b|\bRONA\b|\bHOME HARDWARE", null, "housing.home", 0.9),
        new(@"\bSTRUCTUBE|\bWAYFAIR|\bHOMESENSE|\bBED BATH", null, "housing.home", 0.9),
        new(@"\bBEST ?BUY", "Best Buy", "shopping.electronics", 0.95),
        new(@"\bAPPLE ?STORE|\bAPPLE\.COM/(CA|US|UK)|\bAPPLE (ONLINE )?STORE", "Apple Store", "shopping.electronics", 0.93),
        new(@"\bNEWEGG|\bMEMORY EXPRESS|\bCANADA COMPUTERS|\bB ?& ?H PHOTO|\bCURRYS", null, "shopping.electronics", 0.93),
        new(@"\bH ?& ?M\b|\bHM\.COM", "H&M", "shopping.clothing", 0.93),
        new(@"\bZARA\b", "Zara", "shopping.clothing", 0.95),
        new(@"\bUNIQLO", "Uniqlo", "shopping.clothing", 0.97),
        new(@"\bGAP\b|\bOLD NAVY|\bBANANA REPUBLIC", null, "shopping.clothing", 0.9),
        new(@"\bARITZIA|\bLULULEMON|\bNIKE\b|\bADIDAS|\bWINNERS\b|\bMARSHALLS|\bTJ ?MAXX|\bSIMONS\b|\bSPORT ?CHEK|\bPRIMARK|\bNORDSTROM", null, "shopping.clothing", 0.88),
        new(@"\bSHOPPERS DRUG|\bSDM\b", "Shoppers Drug Mart", "health.pharmacy", 0.9),
        new(@"\bCVS\b", "CVS", "health.pharmacy", 0.9),
        new(@"\bWALGREENS", "Walgreens", "health.pharmacy", 0.93),
        new(@"\bREXALL|\bLONDON DRUGS|\bBOOTS\b|\bJEAN COUTU|\bPHARMAPRIX", null, "health.pharmacy", 0.9),
        new(@"\bSEPHORA|\bULTA\b|\bGREAT CLIPS|\bSUPERCUTS|\bBARBER", null, "personal.care", 0.85),

        // Entertainment
        new(@"\bSTEAM(GAMES|POWERED)?\b|\bVALVE\b", "Steam", "entertainment.games", 0.95),
        new(@"\bPLAYSTATION|\bSONY INTERACTIVE|\bPSN\b", "PlayStation", "entertainment.games", 0.95),
        new(@"\bXBOX\b", "Xbox", "entertainment.games", 0.95),
        new(@"\bNINTENDO", "Nintendo", "entertainment.games", 0.95),
        new(@"\bEPIC ?GAMES|\bRIOT ?GAMES|\bBLIZZARD|\bEA \*|\bELECTRONIC ARTS", null, "entertainment.games", 0.93),
        new(@"\bCINEPLEX", "Cineplex", "entertainment.movies", 0.97),
        new(@"\bAMC ?THEATRES?|\bREGAL CINEMA|\bLANDMARK CINEMA|\bODEON|\bVUE CINEMA", null, "entertainment.movies", 0.95),
        new(@"\bTICKETMASTER|\bLIVE ?NATION|\bSTUBHUB|\bEVENTBRITE|\bSEATGEEK", null, "entertainment.events", 0.93),

        // Health and fitness
        new(@"\bGOODLIFE", "GoodLife Fitness", "health.fitness", 0.97),
        new(@"\bPLANET ?FITNESS", "Planet Fitness", "health.fitness", 0.97),
        new(@"\bEQUINOX\b|\bANYTIME FITNESS|\bFIT4LESS|\bCLASSPASS|\bORANGETHEORY|\bLA FITNESS|\bPURE ?GYM|\bPELOTON|\bSTRAVA", null, "health.fitness", 0.93),
        new(@"\bDENTAL|\bDENTIST|\bMEDICAL|\bCLINIC\b|\bPHYSIO|\bOPTOMETR|\bHOSPITAL", null, "health.medical", 0.8),

        // Phone, internet, utilities, insurance
        new(@"\bROGERS\b", "Rogers", "housing.phone-internet", 0.9),
        new(@"\bBELL (CANADA|MOBILITY)|\bBELL\b", "Bell", "housing.phone-internet", 0.85),
        new(@"\bTELUS", "Telus", "housing.phone-internet", 0.93),
        new(@"\bFIDO\b", "Fido", "housing.phone-internet", 0.95),
        new(@"\bKOODO", "Koodo", "housing.phone-internet", 0.97),
        new(@"\bFREEDOM MOBILE", "Freedom Mobile", "housing.phone-internet", 0.97),
        new(@"\bVIRGIN (PLUS|MOBILE)", "Virgin Plus", "housing.phone-internet", 0.95),
        new(@"\bPUBLIC MOBILE", "Public Mobile", "housing.phone-internet", 0.97),
        new(@"\bVERIZON", "Verizon", "housing.phone-internet", 0.95),
        new(@"\bAT ?& ?T\b", "AT&T", "housing.phone-internet", 0.93),
        new(@"\bT-?MOBILE", "T-Mobile", "housing.phone-internet", 0.95),
        new(@"\bCOMCAST|\bXFINITY|\bSPECTRUM\b|\bSHAW\b|\bVIDEOTRON|\bBT GROUP|\bVODAFONE|\bSKY UK", null, "housing.phone-internet", 0.9),
        new(@"\bHYDRO\b|\bTORONTO HYDRO|\bHYDRO ONE|\bBC HYDRO|\bENBRIDGE|\bFORTIS|\bEPCOR|\bATCO|\bCON ?ED|\bPG ?& ?E|\bDUKE ENERGY|\bBRITISH GAS|\bOCTOPUS ENERGY|\bWATER BILL|\bUTILIT", null, "housing.utilities", 0.9),
        new(@"\bINSURANCE|\bASSURANCE|\bINTACT\b|\bAVIVA\b|\bSTATE FARM|\bGEICO|\bALLSTATE|\bPROGRESSIVE INS|\bMANULIFE|\bSUN LIFE|\bCANADA LIFE|\bDESJARDINS INS|\bTD INSURANCE|\bSONNET", null, "financial.insurance", 0.88),

        // Travel
        new(@"\bAIR ?CANADA", "Air Canada", "travel.flights", 0.95),
        new(@"\bWESTJET", "WestJet", "travel.flights", 0.97),
        new(@"\bPORTER AIR", "Porter Airlines", "travel.flights", 0.97),
        new(@"\bFLAIR AIR", "Flair Airlines", "travel.flights", 0.97),
        new(@"\bDELTA AIR|\bUNITED AIR|\bAMERICAN AIR|\bSOUTHWEST AIR|\bJETBLUE|\bBRITISH AIRWAYS|\bEASYJET|\bRYANAIR|\bLUFTHANSA|\bAIR FRANCE|\bKLM\b|\bEMIRATES|\bQATAR AIR|\bTURKISH AIR", null, "travel.flights", 0.95),
        new(@"\bAIRBNB", "Airbnb", "travel.hotels", 0.95),
        new(@"\bVRBO\b", "Vrbo", "travel.hotels", 0.95),
        new(@"\bMARRIOTT|\bHILTON|\bHYATT|\bFAIRMONT|\bHOLIDAY INN|\bBEST WESTERN|\bIHG\b|\bWESTIN|\bSHERATON|\bNOVOTEL|\bHOTEL\b|\bMOTEL\b|\bINN\b", null, "travel.hotels", 0.85),
        new(@"\bBOOKING\.COM|\bEXPEDIA|\bHOTELS\.COM|\bAGODA|\bKAYAK", null, "travel.other", 0.88),
        new(@"\bENTERPRISE RENT|\bHERTZ\b|\bAVIS\b|\bBUDGET RENT|\bNATIONAL CAR", null, "travel.other", 0.88),

        // Education and giving
        new(@"\bUDEMY|\bCOURSERA|\bUNIVERSITY|\bCOLLEGE\b|\bTUITION|\bDUOLINGO|\bMASTERCLASS", null, "personal.education", 0.85),
        new(@"\bDONATION|\bCHARITY|\bRED CROSS|\bUNICEF|\bGOFUNDME|\bUNITED WAY", null, "personal.gifts-donations", 0.85),
    ];

    /// <summary>Generic words that suggest a category when no merchant matched. Lower confidence.</summary>
    public static readonly IReadOnlyList<CatalogEntry> Keywords =
    [
        new(@"\bRESTAURANT|\bBISTRO|\bGRILL\b|\bSUSHI|\bPIZZ|\bTAQUERIA|\bRAMEN|\bPHO\b|\bKITCHEN\b|\bEATERY|\bDINER\b|\bBRASSERIE|\bSHAWARMA|\bBURGER|\bTHAI\b|\bPUB\b|\bBAR ?& ?GRILL|\bBURRITO|\bTACOS?\b|\bNOODLES?\b|\bDUMPLINGS?\b", null, "food.restaurants", 0.7),
        new(@"\bCAFE\b|\bCAFÉ|\bCOFFEE|\bESPRESSO|\bBAKERY", null, "food.coffee", 0.7),
        new(@"\bGROCER|\bSUPERMARKET|\bMARKET ?PLACE FOODS|\bFOODS? MARKET|\bBUTCHER", null, "food.groceries", 0.7),
        new(@"\bPHARMACY|\bPHARMACIE|\bDRUG ?STORE|\bDRUG MART|\bCHEMIST", null, "health.pharmacy", 0.75),
        new(@"\bPARKING|\bPARK ?N ?FLY", null, "transportation.parking", 0.8),
        new(@"\bGAS ?STATION|\bFUEL\b|\bPETROL", null, "transportation.fuel", 0.75),
        new(@"\bAUTO ?(REPAIR|SERVICE|PARTS)|\bCAR ?WASH|\bOIL CHANGE|\bTIRE\b|\bMECHANIC", null, "transportation.car", 0.75),
        new(@"\bAIRLINE|\bAIRWAYS", null, "travel.flights", 0.75),
        new(@"\bGYM\b|\bFITNESS|\bYOGA|\bPILATES|\bCROSSFIT", null, "health.fitness", 0.75),
        new(@"\bCINEMA|\bTHEATRE|\bTHEATER", null, "entertainment.movies", 0.65),
        new(@"\bSUBSCRIPTION|\bMEMBERSHIP", null, CategoryTaxonomy.Subscriptions, 0.6),
        new(@"\bSTREAMING\b", null, CategoryTaxonomy.Subscriptions, 0.65),
        new(@"\bTAXI\b|\bTAXICAB", null, "transportation.ride-sharing", 0.75),
        new(@"\bHAIR ?(SALON|CUT|STUDIO)|\bSALON\b|\bNAIL (BAR|SALON|SPA)|\bDAY ?SPA|\bLAUNDROMAT|\bDRY ?CLEAN", null, "personal.care", 0.7),
        new(@"\bBOOKSTORE|\bBOOKSHOP|\bINDIGO BOOKS|\bCHAPTERS\b|\bPETSMART|\bPET ?VALU|\bPETCO\b|\bCANADA POST|\bUSPS\b|\bFEDEX|\bPUROLATOR", null, "shopping.general", 0.7),
        new(@"\bHARDWARE\b|\bGARDEN CENT", null, "housing.home", 0.7),
        new(@"\bELECTRICITY|\bNATURAL GAS|\bWATER (AND|&) SEWER", null, "housing.utilities", 0.7),
        new(@"\bWIRELESS\b|\bINTERNET (SERVICE|BILL)", null, "housing.phone-internet", 0.7),
        new(@"\bTOLL ROAD|\bCAR RENTAL|\bRENT ?A ?CAR", null, "travel.other", 0.7),
    ];
}
