import { DOCUMENT, Injectable, PLATFORM_ID, inject, signal } from '@angular/core';
import { isPlatformBrowser } from '@angular/common';

export type Lang = 'en' | 'pcm' | 'yo' | 'ig' | 'ha';
export const LANGS: { code: Lang; label: string }[] = [
  { code: 'en', label: 'English' }, { code: 'pcm', label: 'Pidgin' }, { code: 'yo', label: 'Yorùbá' }, { code: 'ig', label: 'Igbo' }, { code: 'ha', label: 'Hausa' },
];

type Entry = Record<Lang, string>;
const KEY = 'chewypet.lang';

/**
 * The website in the five languages the chat already speaks. English is the source; the others were drafted to match it and should be read by a
 * native speaker before a campaign leans on them. A missing translation falls back to English, never to a key.
 */
const T: Record<string, Entry> = {
  // navigation
  'nav.shop': { en: 'Shop', pcm: 'Shop', yo: 'Ra ọjà', ig: 'Zụta', ha: 'Siyayya' },
  'nav.range': { en: 'Range', pcm: 'Our range', yo: 'Àwọn ọjà wa', ig: 'Ngwaahịa anyị', ha: 'Kayanmu' },
  'nav.cages': { en: 'Cages', pcm: 'Cages', yo: 'Àgò', ig: 'Ụlọ anụ', ha: 'Keji' },
  'nav.why': { en: 'Why Chewy Pet', pcm: 'Why Chewy Pet', yo: 'Kí ló dé Chewy Pet', ig: 'Gịnị mere Chewy Pet', ha: 'Me yasa Chewy Pet' },
  'nav.team': { en: 'Team', pcm: 'Team', yo: 'Ẹgbẹ́ wa', ig: 'Ndị otu anyị', ha: 'Ma\'aikatanmu' },
  'nav.reviews': { en: 'Reviews', pcm: 'Reviews', yo: 'Èrò àwọn oníbàárà', ig: 'Echiche ndị ahịa', ha: 'Ra\'ayoyin abokan ciniki' },
  'nav.sister': { en: 'Candid Purrfect', pcm: 'Candid Purrfect', yo: 'Candid Purrfect', ig: 'Candid Purrfect', ha: 'Candid Purrfect' },
  'nav.chat': { en: 'Chat on WhatsApp', pcm: 'Chat for WhatsApp', yo: 'Bá wa sọ̀rọ̀ lórí WhatsApp', ig: 'Kparịta ụka na WhatsApp', ha: 'Yi hira a WhatsApp' },
  'nav.language': { en: 'Language', pcm: 'Language', yo: 'Èdè', ig: 'Asụsụ', ha: 'Harshe' },

  // hero
  'hero.line1': { en: 'Complete nutrition,', pcm: 'Correct food,', yo: 'Oúnjẹ tó péye,', ig: 'Nri zuru ezu,', ha: 'Cikakken abinci,' },
  'hero.line2': { en: 'every stage.', pcm: 'every stage.', yo: 'ní gbogbo ìpele.', ig: 'n\'oge ọ bụla.', ha: 'a kowane mataki.' },
  'hero.sub': {
    en: "Premium dog food from puppy to senior, produced and packed in Nigeria. See what's in stock and order in minutes.",
    pcm: 'Better dog food from puppy reach old age, we produce and pack am for Nigeria. See wetin dey stock and order sharp sharp.',
    yo: 'Oúnjẹ ajá tó dára láti ọmọ ajá dé àgbàlagbà, tí a ṣe tí a sì dì ní Nàìjíríà. Wo ohun tó wà ní ìpamọ́, kí o sì paṣẹ láàárín ìṣẹ́jú díẹ̀.',
    ig: 'Nri nkịta dị mma site na nwa nkịta ruo agadi, emepụtara ma kwakọọ na Naịjirịa. Lee ihe dị n\'ahịa ma tinye order n\'ime nkeji ole na ole.',
    ha: 'Ingantaccen abincin kare daga ɗan kwikwiyo har zuwa babba, an yi shi kuma an shirya shi a Najeriya. Duba abin da ke akwai ka yi oda cikin mintuna.',
  },
  'hero.shop': { en: "Shop what's in stock", pcm: 'See wetin dey stock', yo: 'Ra ohun tó wà', ig: 'Zụta ihe dị', ha: 'Sayi abin da ke akwai' },

  // shop with us
  'stores.eyebrow': { en: 'Shop with us', pcm: 'Buy from us', yo: 'Ra ọjà lọ́wọ́ wa', ig: 'Zụta n\'aka anyị', ha: 'Yi siyayya da mu' },
  'stores.title': { en: "Everything in stock, at today's prices.", pcm: 'Everything wey dey stock, with today price.', yo: 'Gbogbo ohun tó wà, ní iye owó òní.', ig: 'Ihe niile dị n\'ahịa, na ọnụahịa taa.', ha: 'Duk abin da ke akwai, a farashin yau.' },
  'stores.q1': { en: 'Would you like to see our inventory, to buy or to ask about a product?', pcm: 'You wan see wetin we get, to buy or ask about any product?', yo: 'Ṣé o fẹ́ wo àwọn ọjà wa, láti rà tàbí láti béèrè nípa ọjà kan?', ig: 'Ị chọrọ ịhụ ngwaahịa anyị, ịzụta ma ọ bụ ịjụ maka ngwaahịa?', ha: 'Kana son ganin kayanmu, don saya ko tambaya game da wani kaya?' },
  'stores.q1.desc': { en: 'See what {name} has in stock right now, choose your quantities, and send your order on WhatsApp in one tap.', pcm: 'See wetin {name} get for stock now, choose how many you want, and send your order for WhatsApp with one touch.', yo: 'Wo ohun tí {name} ní báyìí, yan iye tí o fẹ́, kí o sì fi àṣẹ rẹ ránṣẹ́ lórí WhatsApp.', ig: 'Lee ihe {name} nwere ugbu a, họrọ ọnụọgụ, ma ziga order gị na WhatsApp otu ugboro.', ha: 'Duba abin da {name} ke da shi yanzu, zaɓi yawan da kake so, ka tura odarka ta WhatsApp da taɓawa ɗaya.' },
  'stores.q1.yes': { en: "Yes, show me what's in stock", pcm: 'Yes, show me wetin dey', yo: 'Bẹ́ẹ̀ni, fi hàn mí', ig: 'Ee, gosi m ihe dị', ha: 'Eh, nuna min abin da ke akwai' },
  'stores.q1.ask': { en: 'I have a question', pcm: 'I get question', yo: 'Mo ní ìbéèrè', ig: 'Enwere m ajụjụ', ha: 'Ina da tambaya' },
  'stores.instock': { en: 'in stock today', pcm: 'dey stock today', yo: 'tó wà lónìí', ig: 'dị taa', ha: 'da ke akwai yau' },
  'stores.q1.open': { en: 'Add what you need. Your basket goes to us on WhatsApp, or pay online now.', pcm: 'Add wetin you need. Send your basket give us for WhatsApp, or pay online now.', yo: 'Fi ohun tí o nílò kún un. Fi apẹ̀rẹ̀ rẹ ránṣẹ́ sí wa lórí WhatsApp, tàbí sanwó lórí ayélujára.', ig: 'Tinye ihe ị chọrọ. Ziga nkata gị na WhatsApp, ma ọ bụ kwụọ ụgwọ n\'ịntanetị ugbu a.', ha: 'Ƙara abin da kake buƙata. Tura kwandonka ta WhatsApp, ko ka biya ta yanar gizo yanzu.' },
  'stores.q2': { en: 'Looking for a wider range of foods and accessories?', pcm: 'You dey find more food and accessories?', yo: 'Ṣé o ń wá oúnjẹ àti ẹ̀ya ẹ̀rọ púpọ̀ sí i?', ig: 'Ị na-achọ nri na ngwa ndị ọzọ?', ha: 'Kana neman ƙarin abinci da kayan haɗi?' },
  'stores.q2.desc': { en: 'Visit our sister store, {name}: dog and cat food from leading brands such as Booster, Optimax, Dekra and Balance, plus treats, cages and accessories.', pcm: 'Check our sister store, {name}: dog and cat food from big brands like Booster, Optimax, Dekra and Balance, plus treats, cages and accessories.', yo: 'Ṣèbẹ̀wò sí ilé ìtajà arábìnrin wa, {name}: oúnjẹ ajá àti ológbò láti àwọn orúkọ bí Booster, Optimax, Dekra àti Balance, pẹ̀lú ìpanu, àgò àti ẹ̀ya ẹ̀rọ.', ig: 'Gaa ụlọ ahịa nwanne anyị, {name}: nri nkịta na nwamba sitere na Booster, Optimax, Dekra na Balance, yana nri ụtọ, ụlọ anụ na ngwa.', ha: 'Ziyarci shagonmu na \'yar\'uwa, {name}: abincin kare da mage daga Booster, Optimax, Dekra da Balance, da kayan ciye-ciye, keji da kayan haɗi.' },
  'stores.q2.yes': { en: 'Yes, show me {name}', pcm: 'Yes, show me {name}', yo: 'Bẹ́ẹ̀ni, fi {name} hàn mí', ig: 'Ee, gosi m {name}', ha: 'Eh, nuna min {name}' },
  'stores.q2.ask': { en: 'Ask about {name}', pcm: 'Ask about {name}', yo: 'Béèrè nípa {name}', ig: 'Jụọ maka {name}', ha: 'Tambaya game da {name}' },
  'stores.q2.open': { en: 'Our sister store. Same WhatsApp line, same assistant: add what you need and send your basket.', pcm: 'Our sister store. Same WhatsApp number, same assistant: add wetin you need and send your basket.', yo: 'Ilé ìtajà arábìnrin wa. Nọ́mbà WhatsApp kan náà: fi ohun tí o nílò kún un kí o sì fi ránṣẹ́.', ig: 'Ụlọ ahịa nwanne anyị. Otu nọmba WhatsApp: tinye ihe ị chọrọ ma ziga nkata gị.', ha: 'Shagonmu na \'yar\'uwa. Lamba ɗaya ta WhatsApp: ƙara abin da kake buƙata ka tura.' },
  'stores.paid': { en: "Thank you! As soon as AlatPay confirms your payment, we'll send your receipt on WhatsApp (and by email if you gave one).", pcm: 'Thank you! As AlatPay confirm your payment, we go send your receipt for WhatsApp (and email if you give us).', yo: 'Ẹ ṣé! Ní kété tí AlatPay bá jẹ́rìí ìsanwó rẹ, a ó fi ìwé ẹ̀rí ránṣẹ́ sí ọ lórí WhatsApp.', ig: 'Daalụ! Ozugbo AlatPay kwadoro ịkwụ ụgwọ gị, anyị ga-eziga akwụkwọ nnata na WhatsApp.', ha: 'Na gode! Da zarar AlatPay ta tabbatar da biyanka, za mu aiko maka da rasit ta WhatsApp.' },

  // shop
  'shop.search': { en: 'Search {n} products', pcm: 'Search {n} products', yo: 'Wá nínú ọjà {n}', ig: 'Chọọ n\'ime ngwaahịa {n}', ha: 'Bincika kaya {n}' },
  'shop.all': { en: 'All', pcm: 'All', yo: 'Gbogbo', ig: 'Niile', ha: 'Duka' },
  'shop.add': { en: 'Add to basket', pcm: 'Add am', yo: 'Fi kún apẹ̀rẹ̀', ig: 'Tinye na nkata', ha: 'Saka a kwando' },
  'shop.few': { en: 'Only a few left', pcm: 'E remain small', yo: 'Díẹ̀ ló kù', ig: 'Ole na ole fọdụrụ', ha: 'Kaɗan ne suka rage' },
  'shop.per': { en: 'per', pcm: 'per', yo: 'fún', ig: 'otu', ha: 'kowane' },
  'shop.more': { en: 'Show more', pcm: 'Show more', yo: 'Fi púpọ̀ sí i hàn', ig: 'Gosi ndị ọzọ', ha: 'Nuna ƙari' },
  'shop.items': { en: 'items', pcm: 'items', yo: 'ọjà', ig: 'ihe', ha: 'kaya' },
  'shop.clear': { en: 'Clear', pcm: 'Clear am', yo: 'Pa rẹ́', ig: 'Kpochapụ', ha: 'Share' },
  'shop.pay': { en: 'Pay online', pcm: 'Pay online', yo: 'Sanwó lórí ayélujára', ig: 'Kwụọ n\'ịntanetị', ha: 'Biya ta yanar gizo' },
  'shop.whatsapp': { en: 'Order on WhatsApp', pcm: 'Order for WhatsApp', yo: 'Paṣẹ lórí WhatsApp', ig: 'Tinye order na WhatsApp', ha: 'Yi oda ta WhatsApp' },
  'shop.note': { en: 'delivery and VAT confirmed in the chat', pcm: 'delivery and VAT go show for the chat', yo: 'a ó jẹ́rìí ìfijíṣẹ́ àti VAT nínú ìjíròrò', ig: 'a ga-akwado nnyefe na VAT na mkparịta ụka', ha: 'za a tabbatar da isarwa da VAT a hira' },
  'shop.back': { en: 'Back to the shop', pcm: 'Go back to shop', yo: 'Padà sí ilé ìtajà', ig: 'Laghachi n\'ahịa', ha: 'Koma shago' },

  // delivery and hours
  'info.eyebrow': { en: 'Delivery & opening hours', pcm: 'Delivery & time wey we open', yo: 'Ìfijíṣẹ́ àti àkókò ìṣísílẹ̀', ig: 'Nnyefe na oge mmeghe', ha: 'Isarwa da lokutan buɗewa' },
  'info.title': { en: 'Fresh stock, delivered to your door.', pcm: 'Fresh stock, we go bring am reach your door.', yo: 'Ọjà tuntun, a ó mú wá sí ẹnu ọ̀nà rẹ.', ig: 'Ngwaahịa ọhụrụ, ezigara n\'ọnụ ụzọ gị.', ha: 'Sabbin kaya, a kawo har ƙofarka.' },
  'info.where': { en: 'Where we deliver', pcm: 'Where we dey deliver', yo: 'Ibi tí a ń fi ránṣẹ́ sí', ig: 'Ebe anyị na-ezipụ', ha: 'Inda muke kai wa' },
  'info.when': { en: "When we're open", pcm: 'When we dey open', yo: 'Ìgbà tí a ṣí sílẹ̀', ig: 'Mgbe anyị meghere', ha: 'Lokacin da muke buɗe' },

  // reviews
  'reviews.eyebrow': { en: 'What pet parents say', pcm: 'Wetin pet owners talk', yo: 'Ohun tí àwọn olóhun ẹranko ń sọ', ig: 'Ihe ndị nwe anụ ụlọ na-ekwu', ha: 'Abin da masu dabbobi ke cewa' },
  'reviews.write': { en: 'Write a review', pcm: 'Write review', yo: 'Kọ èrò rẹ', ig: 'Dee echiche gị', ha: 'Rubuta ra\'ayinka' },

  // footer
  'footer.explore': { en: 'Explore', pcm: 'Explore', yo: 'Ṣàwárí', ig: 'Nyochaa', ha: 'Bincika' },
  'footer.stores': { en: 'Our stores', pcm: 'Our stores', yo: 'Àwọn ilé ìtajà wa', ig: 'Ụlọ ahịa anyị', ha: 'Shagunanmu' },
  'footer.contact': { en: 'Contact', pcm: 'Contact', yo: 'Kàn sí wa', ig: 'Kpọtụrụ anyị', ha: 'Tuntuɓe mu' },
  'footer.privacy': { en: 'Privacy', pcm: 'Privacy', yo: 'Àṣírí', ig: 'Nzuzo', ha: 'Sirri' },
  'footer.terms': { en: 'Terms of sale', pcm: 'Terms of sale', yo: 'Àwọn òfin títà', ig: 'Usoro ire ahịa', ha: 'Sharuɗɗan sayarwa' },
};

@Injectable({ providedIn: 'root' })
export class I18n {
  private readonly doc = inject(DOCUMENT);
  private readonly browser = isPlatformBrowser(inject(PLATFORM_ID));
  readonly lang = signal<Lang>('en');

  constructor() {
    if (!this.browser) return;
    let saved: string | null = null;
    try { saved = localStorage.getItem(KEY); } catch { /* private mode: English */ }
    if (saved && LANGS.some(l => l.code === saved)) this.set(saved as Lang);
  }

  set(l: Lang): void {
    this.lang.set(l);
    this.doc.documentElement.lang = l;
    try { localStorage.setItem(KEY, l); } catch { /* not remembered */ }
  }

  /** The text for a key in the chosen language ({name}-style placeholders filled from vars), falling back to English. */
  t = (key: string, vars?: Record<string, string | number>): string => {
    const e = T[key];
    let s = e ? (e[this.lang()] || e.en) : key;
    if (vars) for (const [k, v] of Object.entries(vars)) s = s.replaceAll(`{${k}}`, String(v));
    return s;
  };
}
