using System;
using System.Collections.Generic;
using System.Linq;

namespace ReignBeta.Shared.Characters
{
    /// <summary>Authored experiences. Locations and names are bindings, not extra catalogue entries.</summary>
    public static class NpcStoryCatalog
    {
        public static readonly string SceneRules = "Stay with this NPC's personal purpose. Respond naturally to the player's actual contribution; thoughtful disagreement, practical alternatives and emotional support can count. Do not demand an exact phrase. Do not count repetition, tangents or a number of messages as completion. Usually use two to six substantive exchanges for a brief experience. Never reveal unreached phases or private facts. Do not invent native travel, purchases, mission victories, consent or rewards. A mechanical request is an existing explicit proposal; preserve its identity and terms. Return evidence only for supplied objectives, quoting an exact accepted player contribution. If evidence is ambiguous, leave the objective unresolved.";
        public static IReadOnlyList<NpcStoryDefinition> All { get; } = Build().AsReadOnly();
        private static List<NpcStoryDefinition> Build()
        {
            var list = new List<NpcStoryDefinition>();
            Brief(list, "cooking", "Steward", @"The ruined stew|You oversalted supper. Ask for a way to rescue it and choose a remedy together.
Breakfast from home|Describe a simple childhood breakfast and work out which ingredients can be replaced on campaign.
A travelling recipe|Invent a meal that keeps through a march; weigh taste against spoilage and preparation time.
The empty spice pouch|Your usual seasoning is gone. Invite the player to help improvise a different flavor.
Cooking for a rival|You volunteered to cook for someone you dislike; discuss hospitality without pretending the dispute is settled.
One pot for everyone|Two companions want incompatible meals. Design a fair shared supper without claiming either has agreed.
The burned crust|You hid a burned loaf out of embarrassment. Admit it and decide whether to salvage or confess the mistake.");
            Brief(list, "food", "Trade", @"A bread worth finding|Ask what makes regional bread distinctive and decide which kind you would like to try someday.
An unfamiliar spice|Describe an unfamiliar market spice without identifying it as fact; compare possible uses and uncertainties.
The prized delicacy|Admit disliking a celebrated food and discuss how to decline it without insulting a host.
The last sweet|You saved one sweet for later. Discuss sharing, saving and small comforts without transferring inventory.
A feast remembered badly|Your memory of a feast conflicts with a recipe you heard; distinguish nostalgia from what you can recall.
Eating on a lean purse|Plan an inexpensive meal you would still enjoy; compare a treat against feeding more people.
Trying a bitter taste|You want to broaden your tastes but dislike bitterness. Propose a modest first experiment.");
            Brief(list, "stars", "Scouting", @"Finding north|Ask the player to explain a way to orient by the night sky; discuss what to do when clouds hide it.
Two sky legends|Share a childhood sky story as folklore and invite a different interpretation without declaring either objectively true.
A shared constellation|Imagine a shape in the stars together and settle on a name whose meaning you both understand.
Waiting for clear skies|A hoped-for stargazing night is clouded. Decide whether to wait, rest or plan another evening.
The moving light|Describe a distant light as uncertain; compare sensible explanations instead of inventing an omen as fact.
Counting the seasons|Talk through how changing skies mark the year and relate it to a personally meaningful season.
Fear of the dark|Admit unease on night watches and ask for a practical habit that could help without belittling the fear.");
            Brief(list, "music", "Charm", @"An unfinished verse|Offer a rough verse and invite a line that fits its meaning; accept an imperfect but personal ending.
Keeping the beat|You lose the rhythm while tapping a tune. Ask for a simple exercise and try it within the scene.
A remembered lullaby|Recall fragments of a lullaby, explain why it matters and choose whether to share the unfinished part.
A song too loud|You enjoy singing but worry about disturbing resting people. Decide when and where practice would be considerate.
Words from another life|A familiar song means something different after campaigning. Explain the change and hear the player's view.
The stubborn melody|You cannot stop repeating a tune. Invite a playful variation that changes its mood.
Singing without boasting|You want to share a song but fear seeming vain. Agree on a small private performance first.");
            Brief(list, "dance", "Charm", @"Private steps|Ask for a discreet partner to rehearse unfamiliar dance steps, allowing mistakes and gentle correction.
Being watched|You froze when watched dancing. Discuss one manageable way to try again without demanding confidence immediately.
Another culture's rhythm|Ask about an unfamiliar dance custom, keeping guesses separate from supplied cultural facts.
Leading and following|Explore discomfort with letting someone else lead a dance and practise a mutually chosen role.
A step without a partner|Work out a short solo exercise so you can practise without waiting for someone else.
An old injury's limit|Discuss adapting a movement to a stated discomfort; do not diagnose or narrate recovery from injury.
The invitation you missed|You declined a dance from nerves and regret it. Decide how you might express interest another time.");
            Brief(list, "games", "Tactics", @"A childhood board game|Teach a simple remembered game and negotiate a rule that the player interprets differently.
The guarded bridge|Pose a small tactical puzzle about guarding two crossings with limited people and examine a proposed tradeoff.
A friendly wager|Propose a contest with a harmless nonmaterial stake; agree fair rules without inventing a money transfer.
Winning too easily|You suspect a friend let you win. Discuss whether kindness or honest competition matters more to you.
A fair handicap|A game feels uneven between novices and experts. Design a handicap both sides could enjoy.
Losing gracefully|You snapped after losing a game. Acknowledge it and consider how to repair the atmosphere.
Inventing a road game|Create a game playable while travelling that does not distract anyone from actual duties.");
            Brief(list, "humor", "Charm", @"Terrible jokes|Offer a deliberately poor joke and invite one back; enjoy the exchange without forcing the player to laugh.
The embarrassing mishap|Tell a modest embarrassing experience as a personal disclosure and let the player respond without escalating humiliation.
A crooked proverb|Misunderstand a proverb and work through its intended meaning with the player.
The joke that stung|Ask whether a joke crossed a line and consider a sincere apology rather than insisting it was harmless.
A harmless imitation|Try an imitation of an invented generic mannerism; discuss when mimicry becomes cruel.
Laughing at yourself|You can tease others but struggle to accept teasing. Reflect on a fair boundary for both directions.
Timing matters|A joke fell flat during a serious moment. Explore why and choose a better way to lighten tension.");
            Brief(list, "reading", "Charm", @"A troubling passage|Bring a short invented passage that unsettled you and explore more than one plausible interpretation.
Reading aloud|Ask to read a few lines aloud and receive patient feedback on clarity rather than ridicule.
A first book|Explain what you hope to find in reading and compare suitable kinds of books without creating a purchase.
An unreliable narrator|A tale's storyteller sounds self-serving. Ask the player which detail makes them doubt it.
The margin note|An anonymous note in an imagined old book provokes curiosity. Discuss possibilities without inventing its author as a real person.
Stopping halfway|You feel obliged to finish a book you dislike. Discuss whether to persist or choose something else.
A different ending|A tale's ending disappointed you. Propose an alternative and explore what it would change.");
            Brief(list, "writing", "Charm", @"An honest apology|Draft an apology that accepts responsibility without demanding forgiveness; discuss the recipient's possible reaction.
An awkward letter|You need to express a difficult feeling without implying a promise you cannot keep. Work on the wording.
A first poem|Share a rough poem and ask for specific, kind criticism rather than automatic praise.
The unsent page|You wrote something in anger. Decide whether to revise, keep or discard it; sending requires a separate action.
A clear instruction|Turn a muddled set of instructions into a short understandable message and check what could be misread.
Signing your own words|You borrowed grand wording that does not sound like you. Rewrite a line in your own voice.
Remembering small things|Discuss keeping a private travel notebook and choose one observation worth recording first.");
            Brief(list, "language", "Charm", @"A respectful greeting|Practise a greeting from a supplied culture and discuss when formal or familiar wording fits.
No exact translation|Explain a feeling your home language names better and find an imperfect but useful explanation.
An accidental insult|You used a phrase that might offend. Separate intent from impact and plan a clarification.
Learning someone's name|Ask for help remembering unfamiliar names without inventing knowledge of people you have not met.
The wrong register|You sounded too formal in a friendly conversation. Try expressing the same thought more naturally.
An accent and a memory|Explain what a familiar accent reminds you of without assigning a stranger a false identity.
Asking without shame|You did not understand a word but pretended you did. Practise asking for clarification honestly.");
            Brief(list, "drawing", "Charm", @"A camp sketch|Describe what catches your eye around camp and invite the player to choose a detail for a small sketch.
An uneven portrait|Attempt a simple portrait in narration and ask which expressive detail matters more than exact likeness.
Seeing the same hill|Compare descriptions of a landscape and notice how two people attend to different details.
Drawing from memory|Try to describe a remembered doorway for a sketch and acknowledge the parts you cannot recall.
The unfinished picture|You keep abandoning drawings when they look wrong. Choose one small part to finish anyway.
Borrowed inspiration|A design resembles another artist's work. Discuss credit and how to make the next attempt your own.
Beauty in ordinary work|Choose an everyday tool or task as a subject and explain why it deserves attention.");
            Brief(list, "crafts", "Crafting", @"A carved charm|Plan a small charm with a personal meaning and choose a simple design you could realistically make.
Braiding a cord|Ask for help following a short braid pattern; allow a mistake and a practical correction.
A cherished repair|A keepsake is damaged. Discuss preserving its worn history versus making it look new.
Choosing the right wood|Compare qualities needed for a small craft project, treating uncertain material knowledge honestly.
The patient stitch|A delicate repair frustrates you. Work out a pace that protects the object and your patience.
A gift made badly|You made a clumsy handmade gift. Discuss whether sincerity excuses poor work or invites another attempt.
Tools borrowed carefully|Plan how to ask to borrow a valued tool and promise only care you can actually provide.");
            Brief(list, "clothing", "Steward", @"Mending on the road|Ask for help deciding how to mend a travelling garment with limited materials.
Festival colors|Choose clothing for a possible festival that reflects your taste and respects known local customs.
An inherited accessory|Explain why an old accessory matters personally without claiming it belongs to a real relative unless established.
Dressed above your means|Discuss pressure to look wealthier than you are and choose a comfortable boundary.
Practical or beautiful|Compare an attractive garment with a practical one for a specific imagined journey.
A patch worth showing|You are embarrassed by a visible patch. Consider making it deliberate rather than hiding it.
Changing your style|You want a small change in appearance. Explore what it expresses without changing native equipment.");
            Brief(list, "animals", "Riding", @"Choosing a pet name|Discuss names for a hoped-for pet and choose what kind of name would suit your character.
A nervous horse|Observe an available horse's nerves and discuss a calm approach without declaring it trained or cured.
An unlikely fondness|Admit curiosity about an animal you once dismissed and explain what changed your attention.
Responsibility before adoption|Consider feeding, travel and care before seeking a pet; a wish does not create ownership.
A frightened stray|Discuss how to help a hypothetical stray without frightening or injuring it.
Reading an animal poorly|You mistook an animal's behavior for affection. Explore alternative explanations without claiming expert certainty.
Missing an old companion|Remember an animal from your past as a private disclosure and explain what companionship meant to you.");
            Brief(list, "riding", "Riding", @"A stubborn riding habit|Ask for feedback about a riding habit and agree on one controlled exercise.
A favorite mount|Describe what you valued in a past mount and hear a different preference.
A measured challenge|Propose a safe riding challenge with boundaries that avoid an uncontrolled race.
Trust at a stream|Discuss hesitation at a stream crossing and a patient approach rather than forcing either horse or rider.
Packing for balance|Work out how an uneven load affects riding and suggest a more balanced arrangement.
Fear after a fall|Admit lingering nerves from a past fall and choose a gradual first step back.
Kindness and firmness|Debate the difference between clear handling and harsh treatment of a mount.");
            Brief(list, "weapons", "OneHanded", @"The balance of a blade|Compare what different sword balances feel suited for and identify a preference to test later.
Care for a worn edge|Discuss maintaining a blade and choose a sensible maintenance habit without granting equipment upgrades.
A weak technique|Admit a recurring mistake in technique and ask for one correction to practise.
Reach versus speed|Explore the tradeoff between reach and speed using a bounded hypothetical fight.
Drawing without flourish|You overcomplicate a movement to look skilled. Consider a simpler, safer technique.
Respecting a beginner|Discuss how you would teach someone who is nervous around weapons without humiliating them.
Knowing when to stop|You tend to train past fatigue. Agree on a sign that should end a practice session.");
            Brief(list, "confidence", "Athletics", @"Keeping your balance|Try a modest balance exercise in the current safe setting and reflect on a useful correction.
Learning to throw|Ask about a controlled throwing exercise and choose a safe target in narration only.
A difficult climb|Discuss a climb you would like to attempt and plan sensible limits before seeking a real route.
Comparing yourself unfairly|You keep measuring your progress against an expert. Choose a more useful personal benchmark.
Accepting a rest|You feel guilty for needing rest. Discuss the difference between recovery and avoidance.
A modest challenge|Set a small physical goal for yourself without promising an unrealistic transformation.
Being the slowest|Admit embarrassment about struggling with a group activity and ask for a considerate way to participate.");
            Brief(list, "medicine", "Medicine", @"A clean bandage|Ask how to prepare a clean bandage and distinguish basic care from treating a serious wound.
Fear of treatment|Explain anxiety about receiving care and discuss what would help you feel informed and respected.
Persuading someone to rest|Rehearse a considerate way to encourage a weary friend to rest without diagnosing them.
Helping without crowding|Discuss how to assist a healer while respecting their space and instructions.
Admitting your limits|You are tempted to offer care beyond your knowledge. Decide when to seek an experienced healer.
A calm bedside voice|Practise reassuring words that acknowledge uncertainty instead of promising a cure.
Packing a care kit|Choose a few ordinary supplies for a travelling care kit, without creating inventory.");
            Brief(list, "scouting", "Scouting", @"Reading the weather|Compare visible weather signs and make a cautious prediction with an acknowledged uncertainty.
Harmless tracks|Discuss what ordinary tracks might reveal and avoid confidently inventing their maker.
A useful landmark|Choose an actual visible landmark and explain how you would describe it to a lost traveller.
Remembering a fork|You confuse similar road junctions. Develop a personal way to remember a route.
Listening before moving|Discuss what quiet observation might reveal and why rushing can miss useful information.
When a shortcut fails|Reflect on a past shortcut that was not worth its risk, without claiming a new route exists.
Reporting uncertainty|Practise a scouting report that distinguishes what you observed from what you inferred.");
            Brief(list, "nature", "Scouting", @"Recognizing birds|Describe a bird you noticed and ask what details would help identify it honestly.
A small flower bundle|Choose a few ordinary flowers for their appearance and meaning without claiming medicinal effects.
A favorite landscape|Explain a landscape that makes you feel at ease and compare what the player finds comforting.
Watching the seasons|Notice a seasonal change in the supplied setting and connect it to a personal habit.
Leaving a place intact|Discuss how to enjoy a quiet natural place without damaging it or taking everything home.
The appeal of rain|Explain why a kind of weather others dislike gives you pleasure, while acknowledging practical discomfort.
Learning one tree|Choose a tree to observe closely and identify what you would like to learn about it.");
            Brief(list, "curiosities", "Scouting", @"An unusual bridge|Ask about the purpose of an unfamiliar bridge feature; separate speculation from known history.
A local custom|Express curiosity about a supplied custom and plan a respectful question for someone who knows it.
An overlooked corner|Discuss the kind of quiet place you would like to explore when there is time.
Following a craft sign|An unfamiliar workshop sign catches your interest. Explain what you hope to learn there.
Why places have names|Discuss possible origins of a place name without declaring an invented etymology true.
Asking a stranger politely|Rehearse how to ask a resident about local life without treating them as a guide on demand.");
            Brief(list, "beliefs", "Charm", @"A harmless superstition|Describe a personal superstition and discuss why it comforts you without presenting magic as fact.
A quiet ritual|Explain a small private ritual and decide whether you want company or privacy while observing it.
Preparing for a festival|Discuss what participation in a known festival would mean to you and what you want to learn.
Respectful disagreement|Explain a belief the player may not share and establish how to discuss it without contempt.
Changing a conviction|Admit that experience challenged an old conviction; explore what you still believe.
Meaning without certainty|Discuss whether a meaningful custom needs a certain explanation to remain worthwhile.");
            Brief(list, "family", "Charm", @"Planning a reunion|Consider what you would say at a family reunion, keeping unestablished relatives unnamed.
A thoughtful gift|Choose the intention behind a family gift before worrying about its cost or purchase.
Living in a sibling's shadow|Discuss feeling compared with a sibling and identify something you value about your own path.
An old household rule|Recall a family rule you now question and consider why it may have existed.
What to leave unsaid|Decide which campaign experiences you would share with family and which deserve privacy.
A lesson worth passing on|Choose a lesson from your upbringing you would want a younger relative to receive differently.");
            Brief(list, "homesickness", "Charm", @"A familiar routine|Recreate a small household routine in camp and explain why it makes the road easier.
A street in memory|Describe a remembered street through ordinary details while admitting what may have changed.
A private anniversary|Tell the player that a date matters to you and choose how much of its meaning to reveal.
The scent of home|An ordinary smell recalls home. Explain the association and hear what triggers memory for the player.
Belonging in two places|Discuss feeling attached both to home and to companions on the road.
A visit imagined honestly|Consider what returning home might be like without assuming everyone would welcome you unchanged.");
            Brief(list, "remembrance", "Charm", @"The keepsake's meaning|Explain the emotional value of an established or explicitly narrative keepsake without fabricating an inventory item.
A story of the departed|Share one gentle memory of a deceased person without turning grief into a quest automatically.
Words for a grave|Choose what you would say at a meaningful grave and allow silence or disagreement.
Remembering imperfectly|Admit that a cherished memory is incomplete and discuss how to honor it honestly.
Keeping a promise in spirit|An old promise cannot be fulfilled literally. Consider a respectful substitute without changing a real agreement.
An ordinary remembrance|Choose a modest annual gesture of remembrance that need not become a public ceremony.");
            Brief(list, "friendship", "Charm", @"A considerate surprise|Plan a surprise suited to a friend's known preferences without assuming their consent to an obligation.
A petty quarrel|Describe your part in a minor disagreement and consider a fair repair without speaking for the absent person.
Overdue gratitude|Find a specific way to thank someone for an established kindness, without inventing a new favor.
Different kinds of company|Discuss why you sometimes want quiet company rather than conversation.
Asking for a boundary|Practise expressing a personal boundary while preserving a friendship.
Trusting a confidence|Discuss how to respond when entrusted with private information without revealing another person's actual secret.");
            Brief(list, "vulnerability", "Charm", @"Admitting a failure|Share a modest failure and distinguish accepting responsibility from condemning yourself entirely.
Retracting a boast|Admit you exaggerated a harmless achievement and decide how to correct the impression.
Accepting help|Explain why receiving help feels difficult and choose a small kind of assistance you could accept.
Being misunderstood|Describe a feeling that others misread and invite the player to ask instead of assuming.
Uncertainty about your place|Admit doubts about what you contribute and explore a concrete strength without demanding praise.
An honest refusal|Rehearse declining a request respectfully when you cannot fulfill it.");
            Brief(list, "ambitions", "Steward", @"A workshop someday|Describe a modest workshop you might like to run and identify the first realistic skill you would need.
Learning an instrument|Choose an instrument you are curious about and consider the patience and practice involved.
Teaching what you know|Discuss whether you would enjoy teaching and how you would treat a struggling learner.
A life beyond campaigning|Imagine an ordinary future day and explain which part you most hope to make real.
Choosing one ambition|Several interests compete for your time. Decide which deserves a small first commitment.
Success on your own terms|Distinguish a personal ambition from a goal you adopted to impress someone else.");
            Brief(list, "ethics", "Charm", @"Lost property|Discuss a hypothetical found purse and weigh returning it, finding its owner and avoiding false claims.
An unfair bargain|Consider a bargain that is legal but exploitative and explain where you would draw a line.
A humiliating joke|Decide how to respond when a group laughs at someone who cannot easily object.
Credit for shared work|Discuss how to acknowledge another person's contribution without minimizing your own.
A small inconvenient truth|Consider whether to admit a minor mistake when concealment would be easier.
Kindness with a limit|Discuss helping someone without promising resources or attention you cannot sustain.");
            Brief(list, "reconciliation", "Charm", @"An overdue apology|Consider an apology you have postponed and name what responsibility it should acknowledge.
An abandoned interest|Explain why you left a hobby behind and decide whether returning to it would still please you.
An estranged friend|Discuss whether you want contact with an old friend and what boundary would make it bearable.
Forgiveness without forgetting|Explore whether you could forgive someone while retaining a protective boundary.
Making peace with a place|Describe why returning to a remembered place feels difficult and choose a manageable first step.
Letting a quarrel end|Consider ending a minor dispute without requiring the other person to declare you entirely right.");
            AddOutings(list);
            AddMajorStories(list);
            foreach (string row in new[] {
                "brief_homesickness_02|outing_storytelling_circle|major_missing_friend",
                "brief_weapons_03|outing_smith_demo|major_training",
                "brief_crafts_01|outing_pottery_firing|major_apprenticeship",
                "brief_family_01|outing_family_visit|major_family_divide",
                "brief_remembrance_01|outing_graveside|major_heirloom",
                "brief_ethics_06|outing_mentor_visit|major_mentor",
                "brief_music_01|outing_poetry_gathering|major_performance",
                "brief_ambitions_01|outing_dye_workshop|major_workshop",
                "brief_remembrance_05|outing_shrine_repair|major_oath",
                "brief_family_04|outing_seed_market|major_inheritance",
                "brief_homesickness_06|outing_coastal_walk|major_return",
                "brief_friendship_02|outing_reconciliation_visit|major_troubling_friend" })
            {
                string[] ids = row.Split('|');
                foreach (string source in ids.Take(2)) list.Single(d => d.Id == source).RelatedMajorId = ids[2];
            }
            list.Single(d => d.Id == "brief_homesickness_02").Phases[0].Prompt +=
                " You remember a welcoming tavern on this street in the supplied connected city. Mention why it appeals to you, without requesting travel or revealing the childhood friend yet.";
            list.Single(d => d.Id == "outing_storytelling_circle").Phases[0].Prompt +=
                " When a connected earlier tavern memory exists, this circle meets at that same tavern. Thank the player for accompanying you; do not reveal a missing friend before the major story is unlocked.";
            foreach (var definition in list)
            {
                definition.Version = 2;
                foreach (var objective in definition.Phases.SelectMany(p => p.Objectives).Where(o => !o.Native && o.CompletionOutcomes.Count == 0))
                    objective.CompletionOutcomes.Add("completed");
            }
            return list;
        }
        private static void Brief(List<NpcStoryDefinition> list, string family, string skill, string rows)
        {
            int index = 0;
            foreach (string row in rows.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string[] p = row.Split('|'); string id = "brief_" + family + "_" + (++index).ToString("00");
                list.Add(new NpcStoryDefinition { Id = id, Title = p[0], Family = family, Interest = family,
                    Phases = new List<NpcStoryPhase> { new NpcStoryPhase { Id = id + "_conversation", Title = p[0], Prompt = p[1] + " " + SceneRules, Skill = skill,
                        Objectives = new List<NpcStoryObjective> { new NpcStoryObjective { Id = id + "_participation", Instruction = p[1] + " Complete only after a relevant player contribution has been explored and the NPC has reached a personal conclusion; agreement is not required." } } } } });
            }
        }
        private static void AddOutings(List<NpcStoryDefinition> list)
        {
            const string rows = @"pet_adoption|A rabbit for the road|animals|Charm|Meet a rabbit keeper, discuss care on the road and choose whether to adopt a rabbit. Adoption is a narrative story outcome after participation, never an invented gold or inventory transfer.
seed_market|Seeds for a future garden|nature|Trade|Visit a seed stall and choose which modest garden you would like to try growing someday.
bookbinder|A book worth repairing|reading|Trade|Watch a bookbinder explain repair choices and decide which parts of an old book matter most.
instrument_maker|Finding your instrument|music|Charm|Meet an instrument maker, compare sounds and choose which instrument you would like to learn.
cultural_dance|An evening on the floor|dance|Charm|Participate in a cultural dance, respect personal boundaries and reflect on one step you learned.
storytelling_circle|A story among strangers|reading|Charm|Listen at a storytelling circle and decide whether to contribute a small story of your own.
poetry_gathering|Finding the courage to read|writing|Charm|Attend a poetry gathering and share a short verse or honestly explain why you are not ready.
harvest_feast|A place at the harvest table|food|Charm|Join an authored local harvest feast and learn how hosts make room for strangers.
smith_demo|Watching a blade take shape|weapons|Crafting|Watch a smith demonstrate one stage of forging and discuss the patience behind the craft.
pottery_firing|Waiting for the kiln|crafts|Crafting|Visit a pottery firing, make a small narrative piece and face the uncertainty of its result.
dye_workshop|Choosing a lasting color|clothing|Trade|Visit a dyer and compare a desired color with the work and materials needed to produce it.
mill_visit|How a town is fed|cooking|Steward|Visit a mill and ask how its workers balance ordinary demand, maintenance and bad weather.
board_game_hall|Playing beyond the camp|games|Tactics|Play an authored friendly board game in a local hall and learn from a different style of play.
archery_meet|A steadier aim|confidence|Bow|Attend a friendly narrated archery demonstration and practise one correction without creating a native tournament result.
horse_fair|More than a handsome horse|riding|Riding|Visit a horse fair and discuss temperament and care before appearances; a purchase requires its own verified trade.
wrestling_exhibition|Balance before strength|confidence|Athletics|Observe a wrestling exhibition and try a safe narrated balance exercise; do not claim a native combat victory.
stargazing_hill|A sky without rooftops|stars|Scouting|Visit a suitable nearby viewpoint after dark and share a quiet observation rather than inventing a celestial event.
coastal_walk|The sound of the coast|nature|Scouting|Walk an actually coastal destination and compare what the sea makes each of you curious about.
spring_pilgrimage|A spring worth the journey|beliefs|Charm|Visit a known spring as a personal observance, keeping folklore separate from supernatural certainty.
orchard_harvest|A morning in the orchard|nature|Athletics|Help at an authored orchard activity and discuss the satisfaction and fatigue of ordinary seasonal work.
family_visit|Time with family|family|Charm|Visit an authored or established family contact, respecting what the NPC has chosen to disclose and allowing a private part of the visit.
mentor_visit|Showing what you learned|ambitions|Charm|Visit a stable authored mentor and discuss what has changed since the NPC last studied with them.
graveside|An ordinary remembrance|remembrance|Charm|Reach a meaningful grave and give the NPC space for an observance without manufacturing reconciliation or closure.
reconciliation_visit|A difficult first meeting|reconciliation|Charm|Meet an estranged authored acquaintance and let a small honest exchange matter even if full reconciliation is impossible.
help_healer|Useful hands|medicine|Medicine|Assist a healer with ordinary noncritical tasks and respect instructions rather than claiming cures.
communal_kitchen|A meal made together|cooking|Steward|Help an authored communal kitchen adapt a meal to limited ingredients and acknowledge other workers.
foal_care|Patience with a young animal|animals|Riding|Help an animal keeper with a young foal in a safe narrated scene and learn to respect its limits.
shrine_repair|Care for a shared place|beliefs|Crafting|Help repair a small shrine without assuming shared belief or inventing native construction effects.";
            foreach (string row in rows.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string[] p = row.Split('|'); string id = "outing_" + p[0];
                var phase = new NpcStoryPhase { Id = id + "_visit", Title = p[1], MinimumAffinity = 60, LocationKind = p[0] == "shrine_repair" || p[0] == "orchard_harvest" ? "village" : "settlement", Skill = p[3], Xp = 400, Affinity = 5, Prompt = p[4] + " " + SceneRules,
                    Objectives = new List<NpcStoryObjective> { new NpcStoryObjective { Id = id + "_arrival", Kind = "visit", Instruction = "The player and this NPC must be present at the bound story location." }, new NpcStoryObjective { Id = id + "_activity", Instruction = p[4] } } };
                phase.RequiresCoast = p[0] == "coastal_walk";
                if (p[0] == "pet_adoption")
                {
                    phase.Objectives.Single(o => !o.Native).CompletionOutcomes.AddRange(new[] { "adopted", "declined" });
                    phase.Facts.Add(new NpcLifeFact { Id = "rabbit", Kind = "pet", Text = "You adopted a pet rabbit with the player's help. Your rabbit accompanies you wherever you travel; care for it and remember it in relevant scenes. Preserve its name if established in accepted conversation history; otherwise ask what to call it.", Private = false,
                        OutcomeObjectiveId = id + "_activity", RequiredNarrativeOutcome = "adopted" });
                    phase.Facts.Add(new NpcLifeFact { Id = "rabbit_declined", Kind = "experience", Text = "You and the player visited a rabbit keeper and decided against adoption. You did not acquire a rabbit from this outing.",
                        OutcomeObjectiveId = id + "_activity", RequiredNarrativeOutcome = "declined" });
                }
                list.Add(new NpcStoryDefinition { Id = id, Title = p[1], Category = "outing", Family = p[2], Interest = p[2], Phases = new List<NpcStoryPhase> { phase } });
            }
        }
        private static void AddMajorStories(List<NpcStoryDefinition> list)
        {
            Major(list, "missing_friend", "A place from childhood", "friendship", "Charm", new[] {
                "Describe a tavern from childhood and reveal why returning feels difficult; do not reveal the missing friend yet.",
                "At the bound tavern reveal that a childhood friend shared this place and has vanished. Ask permission to explain a specific clue.",
                "Follow the authored clue to the bound hideout and rescue the stable friend character. Only a verified native rescue resolves this phase.",
                "Reflect on the rescue and what reconnecting may mean, leaving the friend room for their own feelings." }, 2, "hideout");
            Major(list, "apprenticeship", "The unfinished apprenticeship", "crafts", "Crafting", new[] {
                "Admit leaving an apprenticeship after a specific failure; discuss what you still want to learn.",
                "Visit the stable former teacher and request one manageable task without demanding reinstatement.",
                "Complete an authored narrative craft exercise, accept criticism and decide whether to continue learning." });
            Major(list, "family_divide", "Two sides of a household", "family", "Charm", new[] {
                "Explain a family disagreement without assuming the player must support your side.",
                "Visit a stable family contact and hear a conflicting account while preserving unresolved facts.",
                "Choose a limited reconciliation, a respectful boundary or an honest separation; no branch requires total agreement." });
            Major(list, "heirloom", "More than the missing object", "remembrance", "Scouting", new[] {
                "Reveal the meaning of an explicitly bound missing heirloom and distinguish its emotional value from its price.",
                "Visit the bound witness and establish a real clue pointing to the authored theft location.",
                "Recover the bound stolen item from the native hideout; verified possession and encounter outcome are required.",
                "Discuss what recovering the heirloom changes and what it cannot restore." }, 2, "hideout");
            Major(list, "mentor", "The mentor's disgrace", "ethics", "Charm", new[] {
                "Explain why you admired a mentor and why an allegation unsettles that loyalty.",
                "Visit a stable witness and compare the supplied evidence with your preferred explanation.",
                "Meet the mentor and choose support, accountability or distance based on revealed evidence, without inventing legal authority." });
            Major(list, "performance", "The promised performance", "music", "Charm", new[] {
                "Confide a wish to perform a piece that matters personally and explain the fear holding you back.",
                "Visit the bound rehearsal location and work through a specific weakness with the player's support.",
                "Give an authored public performance with imperfect moments and respond to the established audience reaction." });
            Major(list, "workshop", "A room of your own", "ambitions", "Steward", new[] {
                "Describe a realistic workshop ambition and name a skill or resource you lack.",
                "Visit a craftsperson and compare your dream with the daily work it would require.",
                "Complete a modest narrative first project and decide on a next step; actual business ownership requires a separate native purchase." });
            Major(list, "oath", "A companion's last request", "remembrance", "Charm", new[] {
                "Reveal a promise to a deceased authored companion and explain why you postponed it.",
                "Visit the stable intended recipient and discover that their wishes differ from your expectations.",
                "Choose a respectful fulfillment or acknowledged limitation, preserving the recipient's agency." });
            Major(list, "inheritance", "The disputed inheritance", "family", "Trade", new[] {
                "Explain a disputed family keepsake or small holding without granting the player judicial authority.",
                "Visit the bound family gathering and compare the authored claims and evidence.",
                "Negotiate a voluntary narrative settlement or accept unresolved disagreement; native assets change only through verified actions." });
            Major(list, "training", "Testing your training", "weapons", "OneHanded", new[] {
                "Discuss a weakness in sword technique and choose one lesson to practise together.",
                "Ask for a private native practice duel. Learn from participation whether you or the player wins.",
                "Visit the bound bandit hideout together and put the lesson into practice through a real encounter.",
                "Reflect honestly on the verified outcome and identify what you still need to learn." }, 2, "hideout", 1);
            Major(list, "return", "Returning without pretending", "homesickness", "Charm", new[] {
                "Explain why a particular established place is difficult to revisit and negotiate a manageable visit.",
                "Reach the bound place and compare it with your memory, acknowledging changes and uncertainty.",
                "Choose what relationship you want with this place now; comfort is welcome but instant healing is not required." });
            Major(list, "troubling_friend", "A friend's dangerous choice", "friendship", "Charm", new[] {
                "Confide concern about an authored friend's choices without treating rumor as proof.",
                "Meet the stable friend and hear their reasons; keep persuasion focused on the specific harmful choice.",
                "Offer a concrete alternative, set a boundary or accept their refusal. Friendship does not guarantee obedience." });
        }
        private static void Major(List<NpcStoryDefinition> list, string slug, string title, string family, string skill, string[] prompts, int nativePhase = -1, string nativeKind = "", int duelPhase = -1)
        {
            string id = "major_" + slug;
            var d = new NpcStoryDefinition { Id = id, Title = title, Category = "major", Family = family, Interest = family };
            d.SupportingRole = slug == "missing_friend" || slug == "troubling_friend" ? "childhood friend"
                : slug == "apprenticeship" || slug == "mentor" ? "former teacher"
                : slug == "family_divide" || slug == "inheritance" ? "family acquaintance"
                : slug == "oath" ? "the intended recipient" : slug == "heirloom" ? "witness to the theft" : "";
            for (int i = 0; i < prompts.Length; i++)
            {
                string phaseId = id + "_" + (i + 1);
                string kind = i == duelPhase ? "duel" : i == nativePhase ? nativeKind : "discussion";
                var phase = new NpcStoryPhase { Id = phaseId, Title = title + " — " + (i + 1), MinimumAffinity = 80, LocationKind = i == 0 ? "current" : kind == "hideout" ? "hideout" : "settlement", Skill = skill, Xp = kind == "duel" ? 400 : i == 0 ? 25 : 1000, Affinity = i == 0 ? 1 : kind == "duel" ? 5 : 10, Prompt = prompts[i] + " " + SceneRules };
                if (i > 0) phase.Objectives.Add(new NpcStoryObjective { Id = phaseId + "_arrival", Kind = "visit", Instruction = "Verify the player and the story NPC reached the bound location." });
                phase.Objectives.Add(new NpcStoryObjective { Id = phaseId + "_purpose", Kind = kind, Instruction = prompts[i], RequiredOutcome = kind == "hideout" ? slug == "missing_friend" ? "rescued" : slug == "heirloom" ? "recovered" : "won" : "" });
                d.Phases.Add(phase);
            }
            list.Add(d);
        }
        public static IReadOnlyList<string> Validate(IEnumerable<NpcStoryDefinition> definitions)
        {
            var errors = new List<string>(); var ids = new HashSet<string>(StringComparer.Ordinal);
            if (definitions == null) return new[] { "A catalogue cannot be null." };
            foreach (var d in definitions)
            {
                if (d == null) { errors.Add("A story definition cannot be null."); continue; }
                if (d.Phases == null || d.Phases.Count > 24) { errors.Add("A story requires at most 24 phases: " + d.Id); continue; }
                if (d.Id == null || d.Id.Length > 100 || d.Id.Any(c => !char.IsLetterOrDigit(c) && c != '_' && c != '-'))
                    errors.Add("Use a stable alphanumeric story identity: " + d.Id);
                if (d.Interest == null) errors.Add("A story interest cannot be null: " + d.Id);
                if (d.RelatedMajorId == null || d.RelatedMajorId.Length > 100 || d.RelatedMajorId.Length > 0 && !d.RelatedMajorId.StartsWith("major_", StringComparison.Ordinal))
                    errors.Add("A linked experience requires a bounded major-story identity: " + d.Id);
                if (d.SupportingRole == null || d.SupportingRole.Length > 120 || d.SupportingRevealPhase < 0
                    || d.SupportingRole.Length > 0 && d.SupportingRevealPhase >= d.Phases.Count)
                    errors.Add("A supporting character requires a bounded role and reachable reveal phase: " + d.Id);
                if (string.IsNullOrWhiteSpace(d.Id) || !ids.Add(d.Id ?? "")) errors.Add("Missing or duplicate story identity: " + d.Id);
                if (string.IsNullOrWhiteSpace(d.Title) || string.IsNullOrWhiteSpace(d.Family) || d.Version < 1 || !new[] { "brief", "outing", "major", "leave", "independent" }.Contains(d.Category)) errors.Add("Invalid story metadata: " + d.Id);
                if (d.Phases.Count == 0) errors.Add("No phases: " + d.Id);
                var phases = new HashSet<string>(StringComparer.Ordinal); var objectives = new HashSet<string>(StringComparer.Ordinal);
                foreach (var p in d.Phases)
                {
                    if (p == null || p.Objectives == null || p.Facts == null) { errors.Add("A phase and its objectives/facts cannot be null: " + d.Id); continue; }
                    if (p.Prompt == null || p.Prompt.Length > 16000 || p.Title == null || p.Title.Length > 240
                        || p.Objectives.Count > 12 || p.Facts.Count > 24 || p.Xp > 100000 || p.Affinity > 100)
                        errors.Add("A phase exceeds the supported authoring limits: " + d.Id + "/" + p.Id);
                    if (!new[] { "OneHanded", "TwoHanded", "Polearm", "Bow", "Crossbow", "Throwing", "Riding", "Athletics", "Crafting", "Scouting", "Tactics", "Roguery", "Charm", "Leadership", "Trade", "Steward", "Medicine", "Engineering" }.Contains(p.Skill))
                        errors.Add("Unsupported native skill: " + p.Skill);
                    if (string.IsNullOrWhiteSpace(p.Id) || !phases.Add(p.Id) || string.IsNullOrWhiteSpace(p.Prompt) || p.Objectives.Count == 0) errors.Add("Invalid phase: " + d.Id + "/" + p.Id);
                    if (p.Xp < 0 || p.Affinity < 0 || p.MinimumAffinity < -100 || p.MinimumAffinity > 100 || !new[] { "current", "settlement", "town", "village", "castle", "hideout" }.Contains(p.LocationKind)) errors.Add("Invalid phase requirements: " + d.Id + "/" + p.Id);
                    foreach (var o in p.Objectives)
                    {
                        if (o == null) { errors.Add("An objective cannot be null: " + d.Id); continue; }
                        if (string.IsNullOrWhiteSpace(o.Id) || !objectives.Add(o.Id) || string.IsNullOrWhiteSpace(o.Instruction)
                            || o.RequiredOutcome == null || o.Kind == "item" || o.Instruction.Length > 8000 || o.RequiredProgress < 1 || o.RequiredProgress > 6
                            || !new[] { "discussion", "support", "agreement", "activity", "reflection", "visit", "item", "duel", "hideout" }.Contains(o.Kind))
                            errors.Add("Invalid objective: " + d.Id + "/" + o.Id);
                        if (!o.Native && !string.IsNullOrEmpty(o.RequiredOutcome))
                            errors.Add("Conversation progress cannot require a native result: " + d.Id + "/" + o.Id);
                        if (o.CompletionOutcomes == null || o.CompletionOutcomes.Count > 3
                            || o.CompletionOutcomes.Distinct(StringComparer.Ordinal).Count() != o.CompletionOutcomes.Count
                            || o.CompletionOutcomes.Any(outcome => !new[] { "completed", "adopted", "declined" }.Contains(outcome))
                            || o.Native && o.CompletionOutcomes.Count > 0)
                            errors.Add("A narrative conclusion requires supported conversation outcomes: " + d.Id + "/" + o.Id);
                        if (o.Native && !SupportsNativeOutcome(o.Kind, o.RequiredOutcome, requirement: true))
                            errors.Add("A native objective requires one supported encounter result: " + d.Id + "/" + o.Id);
                        if ((o.RequiredOutcome == "rescued" || o.RequiredOutcome == "recovered")
                            && (string.IsNullOrWhiteSpace(d.SupportingRole) || d.SupportingRevealPhase > d.Phases.IndexOf(p)))
                            errors.Add("A rescue or recovery requires its supporting identity before the encounter: " + d.Id + "/" + o.Id);
                        if (o.Kind == "hideout" && p.LocationKind != "hideout")
                            errors.Add("A hideout objective must bind a real hideout: " + d.Id + "/" + o.Id);
                    }
                    var factIds = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var f in p.Facts)
                    {
                        if (f == null || string.IsNullOrWhiteSpace(f.Id) || !factIds.Add(f.Id) || string.IsNullOrWhiteSpace(f.Text) || f.Text.Length > 4000)
                            errors.Add("A lasting fact requires a unique identity and bounded text: " + d.Id + "/" + p.Id);
                        else if (f.OutcomeObjectiveId == null || f.RequiredNarrativeOutcome == null
                            || (f.OutcomeObjectiveId.Length == 0) != (f.RequiredNarrativeOutcome.Length == 0)
                            || f.RequiredNarrativeOutcome.Length > 0 && !p.Objectives.Any(o => o.Id == f.OutcomeObjectiveId
                                && !o.Native && o.CompletionOutcomes != null && o.CompletionOutcomes.Contains(f.RequiredNarrativeOutcome)))
                            errors.Add("A conditional fact requires an outcome from this phase's conversation objective: " + d.Id + "/" + f.Id);
                    }
                }
            }
            return errors.AsReadOnly();
        }
        public static bool SupportsNativeOutcome(string kind, string? outcome, bool requirement = false)
        {
            if (outcome == null) return false;
            if (kind == "visit") return outcome == "" || outcome == "arrived";
            if (kind == "duel") return outcome == "won" || (requirement ? outcome == "" : outcome == "lost" || outcome == "withdrawn");
            if (kind == "hideout") return outcome == "won" || outcome == "rescued" || outcome == "recovered"
                || (requirement ? outcome == "" : outcome == "lost" || outcome == "withdrawn");
            return false;
        }
        // Resolve links against the complete merged catalogue, not one edited row.
        public static IReadOnlyList<string> ValidateLinks(IEnumerable<NpcStoryDefinition> definitions)
        {
            var all = definitions?.Where(d => d != null).ToList() ?? new List<NpcStoryDefinition>();
            return all.Where(d => !string.IsNullOrEmpty(d.RelatedMajorId)
                && !all.Any(target => target.Id == d.RelatedMajorId && target.Category == "major" && target.Id != d.Id))
                .Select(d => "The linked major story does not exist: " + d.Id + " -> " + d.RelatedMajorId).ToList().AsReadOnly();
        }
    }
}
