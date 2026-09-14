using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Xml.Linq;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private const string FoundationTraitRangeVNextModelId = FoundationTraitModelId;
        private const double FoundationTraitRangeVNextContextWeightCap = 0.25d;
        private static readonly string[] FoundationTraitRangeVNextNativeKeys = { "valor", "generosity", "honor", "mercy", "calculating" };
        private static readonly string[] FoundationTraitRangeVNextSkillKeys = { "charm", "leadership", "steward", "trade", "roguery", "tactics", "medicine", "engineering", "scouting", "oneHanded", "twoHanded", "polearm", "bow", "crossbow", "throwing", "riding", "athletics" };

        private sealed class FoundationTraitRangeTermVNext
        {
            public string InputKey;
            public double BaseWeight;
            public double Weight;
            public bool IsContext;
        }

        private sealed class FoundationTraitRangeFormulaVNext
        {
            public string TraitKey;
            public List<FoundationTraitRangeTermVNext> Terms;
        }

        private static readonly object FoundationTraitRangeVNextLock = new object();
        private static Dictionary<string, FoundationTraitRangeFormulaVNext> foundationTraitRangeVNextFormulas;

        private static FoundationTraitRangeTermVNext FoundationTermVNext(string inputKey, double weight, bool context = false)
        {
            return new FoundationTraitRangeTermVNext
            {
                InputKey = inputKey,
                BaseWeight = weight,
                Weight = weight,
                IsContext = context
            };
        }

        private static FoundationTraitRangeFormulaVNext FoundationFormulaVNext(string traitKey, params FoundationTraitRangeTermVNext[] terms)
        {
            List<FoundationTraitRangeTermVNext> normalized = (terms ?? new FoundationTraitRangeTermVNext[0])
                .Select(term => new FoundationTraitRangeTermVNext
                {
                    InputKey = term.InputKey,
                    BaseWeight = term.BaseWeight,
                    Weight = term.BaseWeight,
                    IsContext = term.IsContext
                })
                .ToList();
            double total = normalized.Sum(term => Math.Abs(term.BaseWeight));
            double context = normalized.Where(term => term.IsContext).Sum(term => Math.Abs(term.BaseWeight));
            double signed = total - context;
            if (total <= 0d || signed <= 0d) throw new InvalidOperationException("Foundation vNext formula " + traitKey + " requires signed evidence.");
            double normalizedContextShare = context / total;
            if (normalizedContextShare > FoundationTraitRangeVNextContextWeightCap)
            {
                foreach (FoundationTraitRangeTermVNext term in normalized)
                {
                    double magnitude = term.IsContext
                        ? FoundationTraitRangeVNextContextWeightCap * Math.Abs(term.BaseWeight) / context
                        : (1d - FoundationTraitRangeVNextContextWeightCap) * Math.Abs(term.BaseWeight) / signed;
                    term.Weight = Math.Sign(term.BaseWeight) * magnitude;
                }
            }
            else
            {
                foreach (FoundationTraitRangeTermVNext term in normalized)
                    term.Weight = term.BaseWeight / total;
            }
            return new FoundationTraitRangeFormulaVNext { TraitKey = traitKey, Terms = normalized };
        }

        private static Dictionary<string, FoundationTraitRangeFormulaVNext> GetFoundationTraitRangeVNextFormulas()
        {
            lock (FoundationTraitRangeVNextLock)
            {
                if (foundationTraitRangeVNextFormulas != null) return foundationTraitRangeVNextFormulas;
                List<FoundationTraitRangeFormulaVNext> formulas = new List<FoundationTraitRangeFormulaVNext>
                {
                    FoundationFormulaVNext("curiosity", FoundationTermVNext("native.calculating",.30d), FoundationTermVNext("skill.scouting",.25d), FoundationTermVNext("skill.engineering",.20d), FoundationTermVNext("skill.medicine",.15d)),
                    FoundationFormulaVNext("ambition", FoundationTermVNext("native.calculating",.50d), FoundationTermVNext("skill.leadership",.24d), FoundationTermVNext("context.clanLevel",.10d,true), FoundationTermVNext("native.generosity",-.16d)),
                    FoundationFormulaVNext("honesty", FoundationTermVNext("native.honor",.70d), FoundationTermVNext("skill.roguery",-.15d)),
                    FoundationFormulaVNext("compassion", FoundationTermVNext("native.mercy",.45d), FoundationTermVNext("native.generosity",.35d), FoundationTermVNext("skill.medicine",.15d)),
                    FoundationFormulaVNext("discipline", FoundationTermVNext("native.calculating",.45d), FoundationTermVNext("skill.steward",.25d), FoundationTermVNext("skill.tactics",.20d), FoundationTermVNext("context.age40",.10d,true)),
                    FoundationFormulaVNext("sociability", FoundationTermVNext("native.generosity",.35d), FoundationTermVNext("skill.charm",.35d), FoundationTermVNext("context.isNotable",.15d,true)),
                    FoundationFormulaVNext("emotionalStability", FoundationTermVNext("native.calculating",.50d), FoundationTermVNext("trait.discipline",.30d), FoundationTermVNext("context.age40",.20d,true)),
                    FoundationFormulaVNext("pride", FoundationTermVNext("native.valor",.40d), FoundationTermVNext("native.honor",.30d), FoundationTermVNext("context.clanLevel",.10d,true), FoundationTermVNext("skill.leadership",.20d)),
                    FoundationFormulaVNext("patience", FoundationTermVNext("native.calculating",.55d), FoundationTermVNext("skill.steward",.25d), FoundationTermVNext("context.age40",.15d,true)),
                    FoundationFormulaVNext("socialTrust", FoundationTermVNext("native.generosity",.35d), FoundationTermVNext("native.mercy",.25d), FoundationTermVNext("native.honor",.15d), FoundationTermVNext("skill.roguery",-.10d)),
                    FoundationFormulaVNext("flirtatiousness", FoundationTermVNext("native.calculating",.55d), FoundationTermVNext("skill.charm",.30d), FoundationTermVNext("native.generosity",.15d)),
                    FoundationFormulaVNext("authorityRespect", FoundationTermVNext("native.honor",.45d), FoundationTermVNext("skill.steward",.20d), FoundationTermVNext("context.clanLevel",.10d,true), FoundationTermVNext("context.age40",.05d,true), FoundationTermVNext("skill.roguery",-.20d)),
                    FoundationFormulaVNext("assertiveness", FoundationTermVNext("native.valor",.40d), FoundationTermVNext("skill.leadership",.25d), FoundationTermVNext("context.clanLevel",.05d,true), FoundationTermVNext("native.mercy",-.30d)),
                    FoundationFormulaVNext("tact", FoundationTermVNext("native.calculating",.40d), FoundationTermVNext("skill.charm",.35d), FoundationTermVNext("skill.steward",.10d), FoundationTermVNext("native.valor",-.15d)),
                    FoundationFormulaVNext("loyalty", FoundationTermVNext("native.honor",.45d), FoundationTermVNext("native.generosity",.35d)),
                    FoundationFormulaVNext("vengefulness", FoundationTermVNext("native.mercy",-.55d), FoundationTermVNext("native.valor",.25d), FoundationTermVNext("trait.pride",.20d)),
                    FoundationFormulaVNext("wealthMotivation", FoundationTermVNext("native.generosity",-.35d), FoundationTermVNext("skill.trade",.40d), FoundationTermVNext("context.isNotable",.20d,true)),
                    FoundationFormulaVNext("powerMotivation", FoundationTermVNext("trait.ambition",.55d), FoundationTermVNext("trait.pride",.10d), FoundationTermVNext("skill.leadership",.10d), FoundationTermVNext("context.clanLevel",.05d,true), FoundationTermVNext("native.generosity",-.20d)),
                    FoundationFormulaVNext("familyMotivation", FoundationTermVNext("native.generosity",.45d), FoundationTermVNext("native.honor",.25d)),
                    FoundationFormulaVNext("fameMotivation", FoundationTermVNext("native.valor",.40d), FoundationTermVNext("skill.leadership",.25d), FoundationTermVNext("context.clanLevel",.10d,true), FoundationTermVNext("trait.pride",.25d)),
                    FoundationFormulaVNext("knowledgeMotivation", FoundationTermVNext("native.calculating",.25d), FoundationTermVNext("skill.engineering",.25d), FoundationTermVNext("skill.medicine",.20d), FoundationTermVNext("skill.scouting",.20d), FoundationTermVNext("skill.steward",.10d)),
                    FoundationFormulaVNext("religionMotivation", FoundationTermVNext("native.honor",.40d), FoundationTermVNext("context.age40",.20d,true), FoundationTermVNext("context.clanLevel",.15d,true)),
                    FoundationFormulaVNext("revengeMotivation", FoundationTermVNext("trait.vengefulness",.55d), FoundationTermVNext("trait.pride",.20d), FoundationTermVNext("native.mercy",-.20d)),
                    FoundationFormulaVNext("dutyMotivation", FoundationTermVNext("native.honor",.25d), FoundationTermVNext("skill.steward",.15d), FoundationTermVNext("context.clanLevel",.10d,true), FoundationTermVNext("skill.leadership",.10d), FoundationTermVNext("trait.discipline",.25d), FoundationTermVNext("trait.impulsiveness",-.15d)),
                    FoundationFormulaVNext("survivalMotivation", FoundationTermVNext("native.valor",-.25d), FoundationTermVNext("native.calculating",.20d), FoundationTermVNext("context.isWanderer",.25d,true)),
                    FoundationFormulaVNext("legacyMotivation", FoundationTermVNext("trait.ambition",.27d), FoundationTermVNext("trait.dutyMotivation",.18d), FoundationTermVNext("trait.fameMotivation",.08d), FoundationTermVNext("skill.leadership",.03d), FoundationTermVNext("context.clanLevel",.04d,true), FoundationTermVNext("context.age40",.05d,true), FoundationTermVNext("trait.impulsiveness",-.35d)),
                    FoundationFormulaVNext("riskTolerance", FoundationTermVNext("native.valor",.65d), FoundationTermVNext("trait.ambition",.20d), FoundationTermVNext("trait.survivalMotivation",-.15d)),
                    FoundationFormulaVNext("impulsiveness", FoundationTermVNext("native.calculating",-.65d), FoundationTermVNext("skill.steward",-.15d), FoundationTermVNext("native.valor",.20d)),
                    FoundationFormulaVNext("pragmatism", FoundationTermVNext("native.calculating",.55d), FoundationTermVNext("skill.trade",.25d), FoundationTermVNext("skill.steward",.20d)),
                    FoundationFormulaVNext("traditionalism", FoundationTermVNext("native.honor",.40d), FoundationTermVNext("context.age40",.25d,true), FoundationTermVNext("context.clanLevel",.20d,true), FoundationTermVNext("trait.curiosity",-.15d), FoundationTermVNext("trait.knowledgeMotivation",-.20d)),
                    FoundationFormulaVNext("aggression", FoundationTermVNext("native.valor",.40d), FoundationTermVNext("native.mercy",-.45d), FoundationTermVNext("skill.tactics",.20d), FoundationTermVNext("skill.martial",.15d)),
                    FoundationFormulaVNext("greed", FoundationTermVNext("native.generosity",-.60d), FoundationTermVNext("skill.trade",.25d), FoundationTermVNext("trait.wealthMotivation",.15d)),
                    FoundationFormulaVNext("envy", FoundationTermVNext("native.generosity",-.50d), FoundationTermVNext("trait.pride",.25d), FoundationTermVNext("trait.fameMotivation",.25d)),
                    FoundationFormulaVNext("jealousy", FoundationTermVNext("trait.envy",.80d), FoundationTermVNext("trait.pride",.03d), FoundationTermVNext("trait.flirtatiousness",.02d), FoundationTermVNext("trait.confidence",-.12d), FoundationTermVNext("trait.socialTrust",-.03d)),
                    FoundationFormulaVNext("empathy", FoundationTermVNext("native.mercy",.40d), FoundationTermVNext("native.generosity",.35d), FoundationTermVNext("skill.charm",.15d), FoundationTermVNext("skill.medicine",.10d)),
                    FoundationFormulaVNext("optimism", FoundationTermVNext("native.valor",.25d), FoundationTermVNext("trait.emotionalStability",.25d), FoundationTermVNext("native.generosity",.15d)),
                    FoundationFormulaVNext("fearfulness", FoundationTermVNext("native.valor",-.65d), FoundationTermVNext("trait.emotionalStability",-.20d), FoundationTermVNext("trait.survivalMotivation",.15d)),
                    FoundationFormulaVNext("irritability", FoundationTermVNext("native.calculating",-.45d), FoundationTermVNext("trait.pride",.25d), FoundationTermVNext("trait.aggression",.20d)),
                    FoundationFormulaVNext("confidence", FoundationTermVNext("native.valor",.50d), FoundationTermVNext("skill.leadership",.10d), FoundationTermVNext("skill.charm",.025d), FoundationTermVNext("skill.martial",.025d), FoundationTermVNext("trait.fearfulness",-.35d)),
                    FoundationFormulaVNext("shame", FoundationTermVNext("native.honor",.60d), FoundationTermVNext("trait.pride",.03d), FoundationTermVNext("trait.authorityRespect",.03d), FoundationTermVNext("trait.sociability",-.04d), FoundationTermVNext("trait.confidence",-.20d), FoundationTermVNext("trait.emotionalStability",-.10d))
                };
                foundationTraitRangeVNextFormulas = formulas.ToDictionary(formula => formula.TraitKey, formula => formula, StringComparer.OrdinalIgnoreCase);
                return foundationTraitRangeVNextFormulas;
            }
        }

        private static double ClampFoundationSignalVNext(double value)
        {
            return Math.Max(-2d, Math.Min(2d, value));
        }

        private static double NativeFoundationSignalVNext(double nativeLevel)
        {
            if (nativeLevel <= -1d) return -2d;
            if (nativeLevel >= 1d) return 2d;
            return 0d;
        }

        private static double SignedSkillSignalVNext(double level)
        {
            return ClampFoundationSignalVNext((Math.Max(0d, level) - 100d) / 50d);
        }

        private static double ClanLevelSignalVNext(int clanTier)
        {
            if (clanTier >= 6) return 1d;
            if (clanTier == 5) return .60d;
            if (clanTier == 4) return .30d;
            return 0d;
        }

        private static List<string> FoundationTraitRangeVNextEvaluationOrder()
        {
            HashSet<string> deferred = new HashSet<string>(new[] { "powerMotivation", "dutyMotivation", "legacyMotivation", "jealousy", "shame" },StringComparer.OrdinalIgnoreCase);
            List<string> order = CoreTraitKeys.Where(key=>!deferred.Contains(key)).ToList();
            order.Add("powerMotivation");
            order.Add("dutyMotivation");
            order.Add("legacyMotivation");
            order.Add("jealousy");
            order.Add("shame");
            return order;
        }

        private static double EvaluateFoundationFormulaVNext(FoundationTraitRangeFormulaVNext formula, Func<string, double> resolve)
        {
            return formula.Terms.Sum(term => term.Weight * ClampFoundationSignalVNext(resolve(term.InputKey)));
        }

        private static int QuantizeFoundationFormulaVNext(double score)
        {
            double nearestHalf = Math.Round(score * 2d,0,MidpointRounding.AwayFromZero) / 2d;
            if (Math.Abs(score-nearestHalf)<.000000001d) score=nearestHalf;
            return ClampTrait((int)Math.Round(score, MidpointRounding.AwayFromZero));
        }

        private static Dictionary<string, object> CalculateFoundationTraitsVNext(Dictionary<string, object> hero)
        {
            return CalculateFoundationTraitsVNext(hero, out Dictionary<string, double> ignoredRawScores);
        }

        private static Dictionary<string, object> CalculateFoundationTraitsVNext(Dictionary<string, object> hero, out Dictionary<string, double> rawScores)
        {
            hero = hero ?? new Dictionary<string, object>();
            Dictionary<string, object> native = ReadDictionary(hero, "traits") ?? new Dictionary<string, object>();
            Dictionary<string, object> skills = ReadDictionary(hero, "skills") ?? new Dictionary<string, object>();
            Dictionary<string, double> inputs = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
            {
                ["native.valor"] = NativeFoundationSignalVNext(TraitInt(native,"valor")),
                ["native.generosity"] = NativeFoundationSignalVNext(TraitInt(native,"generosity")),
                ["native.honor"] = NativeFoundationSignalVNext(TraitInt(native,"honor")),
                ["native.mercy"] = NativeFoundationSignalVNext(TraitInt(native,"mercy")),
                ["native.calculating"] = NativeFoundationSignalVNext(TraitInt(native,"calculating")),
                ["skill.charm"] = SignedSkillSignalVNext(ReadDouble(skills,"charm",0d)),
                ["skill.leadership"] = SignedSkillSignalVNext(ReadDouble(skills,"leadership",0d)),
                ["skill.steward"] = SignedSkillSignalVNext(ReadDouble(skills,"steward",0d)),
                ["skill.trade"] = SignedSkillSignalVNext(ReadDouble(skills,"trade",0d)),
                ["skill.roguery"] = SignedSkillSignalVNext(ReadDouble(skills,"roguery",0d)),
                ["skill.tactics"] = SignedSkillSignalVNext(ReadDouble(skills,"tactics",0d)),
                ["skill.medicine"] = SignedSkillSignalVNext(ReadDouble(skills,"medicine",0d)),
                ["skill.engineering"] = SignedSkillSignalVNext(ReadDouble(skills,"engineering",0d)),
                ["skill.scouting"] = SignedSkillSignalVNext(ReadDouble(skills,"scouting",0d)),
                ["context.isNotable"] = ReadBool(hero,"isNotable",false) ? 1d : 0d,
                ["context.isWanderer"] = ReadBool(hero,"isWanderer",false) ? 1d : 0d,
                ["context.age40"] = ReadDouble(hero,"age",0d) >= 40d ? 1d : 0d,
                ["context.clanLevel"] = ClanLevelSignalVNext(ReadInt(hero,"clanTier",0))
            };
            string[] martialSkills = { "oneHanded", "twoHanded", "polearm", "bow", "crossbow", "throwing", "riding", "athletics" };
            inputs["skill.martial"] = martialSkills.Select(key => SignedSkillSignalVNext(ReadDouble(skills,key,0d))).Average();
            Dictionary<string, object> values = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            rawScores = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, FoundationTraitRangeFormulaVNext> formulas = GetFoundationTraitRangeVNextFormulas();
            foreach (string key in FoundationTraitRangeVNextEvaluationOrder())
            {
                if (key.Equals("courage",StringComparison.OrdinalIgnoreCase)) { rawScores[key] = inputs["native.valor"]; values[key] = ClampTrait((int)inputs["native.valor"]); continue; }
                if (key.Equals("mercy",StringComparison.OrdinalIgnoreCase)) { rawScores[key] = inputs["native.mercy"]; values[key] = ClampTrait((int)inputs["native.mercy"]); continue; }
                if (key.Equals("generosity",StringComparison.OrdinalIgnoreCase)) { rawScores[key] = inputs["native.generosity"]; values[key] = ClampTrait((int)inputs["native.generosity"]); continue; }
                FoundationTraitRangeFormulaVNext formula = formulas[key];
                double raw = EvaluateFoundationFormulaVNext(formula, inputKey =>
                {
                    if (inputKey.StartsWith("trait.",StringComparison.OrdinalIgnoreCase)) return TraitInt(values,inputKey.Substring("trait.".Length));
                    return inputs.TryGetValue(inputKey,out double value) ? value : 0d;
                });
                rawScores[key] = raw;
                values[key] = QuantizeFoundationFormulaVNext(raw);
            }
            return values;
        }

        private static List<Dictionary<string, object>> FoundationTraitRangeVNextReachability()
        {
            Dictionary<string, FoundationTraitRangeFormulaVNext> formulas = GetFoundationTraitRangeVNextFormulas();
            List<Dictionary<string, object>> rows = new List<Dictionary<string, object>>();
            foreach (string trait in CoreTraitKeys)
            {
                List<Dictionary<string, object>> witnesses = new List<Dictionary<string, object>>();
                if (!formulas.TryGetValue(trait,out FoundationTraitRangeFormulaVNext formula))
                {
                    foreach (int target in new[] { -2, -1, 0, 1, 2 })
                    {
                        bool nativeLevel=target==-2||target==0||target==2;
                        witnesses.Add(new Dictionary<string, object>{{"target",target},{"level",target},{"raw",target},{"generated",nativeLevel},{"source",nativeLevel?"native_full_range_signal":"editor_override"},{"inputs",new Dictionary<string,object>{{nativeLevel?"native":"foundationOverride",nativeLevel?target/2:target}}}});
                    }
                }
                else
                {
                    foreach (int target in new[] { -2, -1, 0, 1, 2 })
                    {
                        bool found=TryFoundationTraitRangeVNextWitness(formula,target,out Dictionary<string,double> inputs,out double raw);
                        witnesses.Add(new Dictionary<string, object>
                        {
                            ["target"] = target,
                            ["level"] = found ? QuantizeFoundationFormulaVNext(raw) : target,
                            ["raw"] = found ? (object)Math.Round(raw,6) : target,
                            ["generated"] = found,
                            ["source"] = found ? "valid_normalized_inputs" : "editor_override",
                            ["inputs"] = found ? (object)inputs.ToDictionary(pair => pair.Key,pair => (object)pair.Value,StringComparer.OrdinalIgnoreCase) : new Dictionary<string,object>{{"foundationOverride",target}}
                        });
                    }
                }
                rows.Add(new Dictionary<string, object>
                {
                    ["trait"] = trait,
                    ["reachable"] = witnesses.All(row => ReadInt(row,"target",99) == ReadInt(row,"level",98)),
                    ["calculatedReachable"] = witnesses.All(row=>ReadBool(row,"generated",false)),
                    ["calculatedLevels"] = witnesses.Where(row=>ReadBool(row,"generated",false)).Select(row=>ReadInt(row,"level",99)).ToList(),
                    ["levels"] = witnesses.Select(row => ReadInt(row,"level",99)).ToList(),
                    ["witnesses"] = witnesses
                });
            }
            return rows;
        }

        private static bool TryFoundationTraitRangeVNextWitness(FoundationTraitRangeFormulaVNext formula, int target, out Dictionary<string,double> witness, out double raw)
        {
            Dictionary<string,double> localWitness = new Dictionary<string,double>(StringComparer.OrdinalIgnoreCase);
            double localRaw = 0d;
            witness = localWitness;
            raw = localRaw;
            if (formula == null || formula.Terms == null) return false;
            Func<FoundationTraitRangeTermVNext,IEnumerable<double>> allowed = term =>
            {
                if (term.IsContext) return new[] { 0d, 1d };
                if (term.InputKey.StartsWith("native.",StringComparison.OrdinalIgnoreCase)) return new[] { -2d, 0d, 2d };
                return new[] { -2d, -1d, 0d, 1d, 2d };
            };
            Func<int,bool> search = null;
            search = index =>
            {
                if (index >= formula.Terms.Count)
                {
                    double candidate=EvaluateFoundationFormulaVNext(formula,key=>localWitness.TryGetValue(key,out double value)?value:0d);
                    if (QuantizeFoundationFormulaVNext(candidate)!=target) return false;
                    localRaw=candidate;return true;
                }
                FoundationTraitRangeTermVNext term=formula.Terms[index];
                foreach(double value in allowed(term).OrderByDescending(value=>target==0?-Math.Abs(value):Math.Sign(target)*Math.Sign(term.Weight)*value))
                {
                    localWitness[term.InputKey]=value;
                    if(search(index+1))return true;
                }
                localWitness.Remove(term.InputKey);return false;
            };
            bool found=search(0);
            if(!found)localWitness.Clear();
            witness=localWitness;raw=found?localRaw:0d;
            return found;
        }

        private static List<Dictionary<string, object>> FoundationTraitRangeVNextFormulaAudit()
        {
            Dictionary<string, FoundationTraitRangeFormulaVNext> formulas = GetFoundationTraitRangeVNextFormulas();
            return CoreTraitKeys.Select(trait =>
            {
                if (!formulas.TryGetValue(trait,out FoundationTraitRangeFormulaVNext formula))
                    return new Dictionary<string, object>{{"trait",trait},{"lockedNative",true},{"weightPercent",100d},{"contextWeightPercent",0d},{"terms",new List<object>()}};
                return new Dictionary<string, object>
                {
                    ["trait"] = trait,
                    ["lockedNative"] = false,
                    ["weightPercent"] = Math.Round(100d * formula.Terms.Sum(term => Math.Abs(term.Weight)),8),
                    ["contextWeightPercent"] = Math.Round(100d * formula.Terms.Where(term => term.IsContext).Sum(term => Math.Abs(term.Weight)),8),
                    ["terms"] = formula.Terms.Select(term => new Dictionary<string, object>
                    {
                        ["input"] = term.InputKey,
                        ["baseWeight"] = term.BaseWeight,
                        ["weightPercent"] = Math.Round(100d * Math.Abs(term.Weight),8),
                        ["direction"] = term.Weight < 0d ? "inverse" : "direct",
                        ["context"] = term.IsContext
                    }).ToList()
                };
            }).ToList();
        }

        private static Dictionary<string, Dictionary<string, object>> LoadFoundationTraitRangeVNextSkillTemplates(string sandboxData, out string sourcePath, out string error)
        {
            sourcePath = string.IsNullOrWhiteSpace(sandboxData) ? "" : Path.Combine(sandboxData, "sandbox_skill_sets.xml");
            error = "";
            Dictionary<string, Dictionary<string, object>> result = new Dictionary<string, Dictionary<string, object>>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(sourcePath)) { error = "No --sandbox-data path was supplied."; return result; }
            if (!File.Exists(sourcePath)) { error = "Skill-template source was not found: " + sourcePath; return result; }
            try
            {
                XDocument document = XDocument.Load(sourcePath);
                foreach (XElement skillSet in document.Descendants("SkillSet").Where(x => x.Attribute("id") != null))
                {
                    Dictionary<string, object> skills = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                    foreach (XElement skill in skillSet.Elements("skill"))
                        skills[NormalizeSkillKey((string)skill.Attribute("id"))] = ParseInt((string)skill.Attribute("value"));
                    result[(string)skillSet.Attribute("id")] = skills;
                }
            }
            catch (Exception ex) { error = ex.Message; }
            return result;
        }

        private static Dictionary<string, object> FoundationLevelCountsVNext(Dictionary<int, int> counts)
        {
            return new Dictionary<string, object>
            {
                ["minus2"] = counts[-2], ["minus1"] = counts[-1], ["zero"] = counts[0], ["plus1"] = counts[1], ["plus2"] = counts[2],
                ["negativeExtremeCount"] = counts[-2], ["positiveExtremeCount"] = counts[2]
            };
        }

        private static Dictionary<string, object> NumericRangeVNext(IEnumerable<double> values)
        {
            List<double> list = (values ?? Enumerable.Empty<double>()).ToList();
            return new Dictionary<string, object>
            {
                ["count"] = list.Count,
                ["minimum"] = list.Count == 0 ? (object)null : Math.Round(list.Min(), 6),
                ["maximum"] = list.Count == 0 ? (object)null : Math.Round(list.Max(), 6),
                ["mean"] = list.Count == 0 ? (object)null : Math.Round(list.Average(), 6)
            };
        }

        private static Dictionary<string, object> CourtScoreDistributionVNext(List<int> scores, List<double> rawScores)
        {
            scores = scores ?? new List<int>();
            return new Dictionary<string, object>
            {
                ["count"] = scores.Count,
                ["minimum"] = scores.Count == 0 ? (object)null : scores.Min(),
                ["maximum"] = scores.Count == 0 ? (object)null : scores.Max(),
                ["mean"] = scores.Count == 0 ? (object)null : Math.Round(scores.Average(), 6),
                ["negativeExtremeCount"] = scores.Count(x => x <= CourtVirtueNegativeExtremeMaximum),
                ["middleCount"] = scores.Count(x => x > CourtVirtueNegativeExtremeMaximum && x < CourtVirtuePositiveExtremeMinimum),
                ["positiveExtremeCount"] = scores.Count(x => x >= CourtVirtuePositiveExtremeMinimum),
                ["unroundedWeightedAverage"] = NumericRangeVNext(rawScores)
            };
        }

        private static Dictionary<string, object> FoundationTraitRangeVNextProjection(string sandboxData)
        {
            Dictionary<string, Dictionary<string, object>> profiles = LoadCharacterProfileLibrary();
            Dictionary<string, Dictionary<string, object>> skillTemplates = LoadFoundationTraitRangeVNextSkillTemplates(sandboxData, out string skillTemplatePath, out string skillTemplateError);
            Dictionary<string, Dictionary<int, int>> current = CoreTraitKeys.ToDictionary(key => key,key => new Dictionary<int,int>{{-2,0},{-1,0},{0,0},{1,0},{2,0}},StringComparer.OrdinalIgnoreCase);
            Dictionary<string, Dictionary<int, int>> projected = CoreTraitKeys.ToDictionary(key => key,key => new Dictionary<int,int>{{-2,0},{-1,0},{0,0},{1,0},{2,0}},StringComparer.OrdinalIgnoreCase);
            Dictionary<string, Dictionary<string, int>> transitions = CoreTraitKeys.ToDictionary(key => key,key => new Dictionary<string,int>(StringComparer.OrdinalIgnoreCase),StringComparer.OrdinalIgnoreCase);
            Dictionary<string, List<double>> rawFoundation = CoreTraitKeys.ToDictionary(key => key,key => new List<double>(),StringComparer.OrdinalIgnoreCase);
            Dictionary<string, List<double>> currentPercentages = CoreTraitKeys.ToDictionary(key => key,key => new List<double>(),StringComparer.OrdinalIgnoreCase);
            Dictionary<string, List<double>> projectedPercentages = CoreTraitKeys.ToDictionary(key => key,key => new List<double>(),StringComparer.OrdinalIgnoreCase);
            Dictionary<string, List<int>> currentCourt = CourtVirtueKeys.ToDictionary(key => key,key => new List<int>(),StringComparer.OrdinalIgnoreCase);
            Dictionary<string, List<int>> projectedCourt = CourtVirtueKeys.ToDictionary(key => key,key => new List<int>(),StringComparer.OrdinalIgnoreCase);
            Dictionary<string, List<double>> currentCourtRaw = CourtVirtueKeys.ToDictionary(key => key,key => new List<double>(),StringComparer.OrdinalIgnoreCase);
            Dictionary<string, List<double>> projectedCourtRaw = CourtVirtueKeys.ToDictionary(key => key,key => new List<double>(),StringComparer.OrdinalIgnoreCase);
            Dictionary<string, List<int>> courtDeltas = CourtVirtueKeys.ToDictionary(key => key,key => new List<int>(),StringComparer.OrdinalIgnoreCase);
            int storedCompleteCount = 0, templateMergedCount = 0;
            List<string> unresolvedProfiles = new List<string>();

            foreach (Dictionary<string, object> profile in profiles.Values)
            {
                Dictionary<string, object> traitDocument = ReadDictionary(profile,"traits") ?? new Dictionary<string, object>();
                Dictionary<string, object> existing = ReadDictionary(traitDocument,"foundationTraits") ?? new Dictionary<string, object>();
                Dictionary<string, object> existingPercentages = ReadDictionary(traitDocument,"traitPercentages") ?? new Dictionary<string, object>();
                Dictionary<string, object> sourceFacts = ReadDictionary(profile,"sourceFacts") ?? new Dictionary<string, object>();
                Dictionary<string, object> facts = new Dictionary<string, object>(sourceFacts,StringComparer.OrdinalIgnoreCase);
                Dictionary<string, object> storedSkills = new Dictionary<string, object>(ReadDictionary(sourceFacts,"skills") ?? new Dictionary<string, object>(),StringComparer.OrdinalIgnoreCase);
                Dictionary<string, object> resolvedSkills = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                string templateId = ReadString(sourceFacts,"skillTemplateId","");
                bool storedComplete = FoundationTraitRangeVNextSkillKeys.All(storedSkills.ContainsKey);
                if (!string.IsNullOrWhiteSpace(templateId) && skillTemplates.TryGetValue(templateId,out Dictionary<string, object> templateSkills))
                {
                    foreach (KeyValuePair<string,object> pair in templateSkills) resolvedSkills[pair.Key] = pair.Value;
                    templateMergedCount++;
                }
                else if (!string.IsNullOrWhiteSpace(templateId) && !storedComplete) unresolvedProfiles.Add(ReadString(sourceFacts,"heroStringId","unknown"));
                else storedCompleteCount++;
                foreach (KeyValuePair<string,object> pair in storedSkills) resolvedSkills[pair.Key] = pair.Value;
                facts["skills"] = resolvedSkills;

                Dictionary<string, object> calculated = CalculateFoundationTraitsVNext(facts,out Dictionary<string,double> rawScores);
                Dictionary<string, object> draft = new Dictionary<string, object>{{"foundationTraits",calculated},{"hiddenReignTraits",new Dictionary<string,object>(calculated,StringComparer.OrdinalIgnoreCase)}};
                ApplyCatalogTraitEvidence(draft,facts,null);
                calculated = ReadDictionary(draft,"foundationTraits") ?? calculated;
                string heroId = ReadString(sourceFacts,"heroStringId",ReadString(profile,"heroStringId","unknown"));
                Dictionary<string, object> nextPercentages = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                foreach (string trait in CoreTraitKeys)
                {
                    int oldValue = ClampTrait(TraitInt(existing,trait));
                    int newValue = ClampTrait(TraitInt(calculated,trait));
                    int oldPercentage = existingPercentages.ContainsKey(trait) ? ReadInt(existingPercentages,trait,StableTraitPercentage(heroId,trait,oldValue)) : StableTraitPercentage(heroId,trait,oldValue);
                    int newPercentage = RemapTraitPercentage(oldPercentage,oldValue,newValue);
                    current[trait][oldValue]++;
                    projected[trait][newValue]++;
                    string transition = oldValue + ":" + newValue;
                    transitions[trait][transition] = transitions[trait].TryGetValue(transition,out int count) ? count + 1 : 1;
                    rawFoundation[trait].Add(rawScores.TryGetValue(trait,out double raw) ? raw : newValue);
                    currentPercentages[trait].Add(oldPercentage);
                    projectedPercentages[trait].Add(newPercentage);
                    nextPercentages[trait] = newPercentage;
                }

                Dictionary<string, object> storedCourt = ReadDictionary(traitDocument,"courtVirtues") ?? CalculateCourtVirtues(existingPercentages);
                Dictionary<string, object> nextCourt = CalculateCourtVirtues(nextPercentages);
                foreach (string virtue in CourtVirtueKeys)
                {
                    int oldScore = ReadInt(storedCourt,virtue,ReadInt(CalculateCourtVirtues(existingPercentages),virtue,50));
                    int newScore = ReadInt(nextCourt,virtue,50);
                    currentCourt[virtue].Add(oldScore);
                    projectedCourt[virtue].Add(newScore);
                    currentCourtRaw[virtue].Add(CalculateRawCourtVirtueScore(existingPercentages,virtue));
                    projectedCourtRaw[virtue].Add(CalculateRawCourtVirtueScore(nextPercentages,virtue));
                    courtDeltas[virtue].Add(newScore-oldScore);
                }
            }

            List<Dictionary<string, object>> foundationDistribution = CoreTraitKeys.Select(trait => new Dictionary<string, object>
            {
                ["trait"] = trait,
                ["current"] = FoundationLevelCountsVNext(current[trait]),
                ["projected"] = FoundationLevelCountsVNext(projected[trait]),
                ["transitions"] = transitions[trait].Select(pair =>
                {
                    string[] parts=pair.Key.Split(':');
                    return new Dictionary<string,object>{{"from",ParseInt(parts[0])},{"to",ParseInt(parts[1])},{"count",pair.Value}};
                }).OrderBy(row=>ReadInt(row,"from",0)).ThenBy(row=>ReadInt(row,"to",0)).ToList(),
                ["calculationRaw"] = NumericRangeVNext(rawFoundation[trait]),
                ["currentPercentage"] = NumericRangeVNext(currentPercentages[trait]),
                ["projectedPercentage"] = NumericRangeVNext(projectedPercentages[trait]),
                ["observedProjectedLevels"] = new[] {-2,-1,0,1,2}.Where(level=>projected[trait][level]>0).ToList(),
                ["allFiveLevelsObserved"] = new[] {-2,-1,0,1,2}.All(level=>projected[trait][level]>0)
            }).ToList();
            List<Dictionary<string, object>> courtDistribution = CourtVirtueKeys.Select(virtue => new Dictionary<string, object>
            {
                ["virtue"] = virtue,
                ["current"] = CourtScoreDistributionVNext(currentCourt[virtue],currentCourtRaw[virtue]),
                ["projected"] = CourtScoreDistributionVNext(projectedCourt[virtue],projectedCourtRaw[virtue]),
                ["transitions"] = new Dictionary<string,object>
                {
                    ["decreased"] = courtDeltas[virtue].Count(x=>x<0), ["unchanged"] = courtDeltas[virtue].Count(x=>x==0), ["increased"] = courtDeltas[virtue].Count(x=>x>0),
                    ["minimumDelta"] = courtDeltas[virtue].Count==0 ? 0 : courtDeltas[virtue].Min(), ["maximumDelta"] = courtDeltas[virtue].Count==0 ? 0 : courtDeltas[virtue].Max(),
                    ["meanDelta"] = courtDeltas[virtue].Count==0 ? 0d : Math.Round(courtDeltas[virtue].Average(),6)
                }
            }).ToList();
            Dictionary<string, object> skillResolution = new Dictionary<string, object>
            {
                ["complete"] = unresolvedProfiles.Count==0,
                ["sandboxData"] = sandboxData ?? "",
                ["templateSource"] = skillTemplatePath,
                ["templateLoadError"] = skillTemplateError,
                ["loadedTemplateCount"] = skillTemplates.Count,
                ["storedCompleteProfileCount"] = storedCompleteCount,
                ["templateMergedProfileCount"] = templateMergedCount,
                ["unresolvedProfileCount"] = unresolvedProfiles.Count,
                ["unresolvedProfileIds"] = unresolvedProfiles.Take(100).ToList()
            };
            return new Dictionary<string, object>
            {
                ["profileCount"] = profiles.Count,
                ["complete"] = unresolvedProfiles.Count==0,
                ["skillResolution"] = skillResolution,
                ["distribution"] = foundationDistribution,
                ["foundationDistribution"] = foundationDistribution,
                ["courtVirtueDistribution"] = courtDistribution
            };
        }

        private static Dictionary<string, object> FoundationTraitRangeVNextTestHero(string id)
        {
            Dictionary<string, object> skills = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            foreach (string key in new[] { "charm","leadership","steward","trade","roguery","tactics","medicine","engineering","scouting","oneHanded","twoHanded","polearm","bow","crossbow","throwing","riding","athletics" }) skills[key] = 100;
            return new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                ["heroStringId"] = id,
                ["age"] = 35,
                ["isLord"] = true,
                ["isNotable"] = false,
                ["isWanderer"] = false,
                ["clanTier"] = 3,
                ["traits"] = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase){{"valor",1},{"generosity",0},{"honor",1},{"mercy",0},{"calculating",1}},
                ["skills"] = skills
            };
        }

        private static List<Dictionary<string, object>> RunFoundationTraitRangeVNextSelfTests()
        {
            List<Dictionary<string, object>> rows = new List<Dictionary<string, object>>();
            Action<string,bool,string,object> add = (name,passed,detail,data) => rows.Add(new Dictionary<string, object>{{"name","foundation_vnext_"+name},{"passed",passed},{"detail",detail},{"data",data}});
            List<Dictionary<string, object>> formulaAudit = FoundationTraitRangeVNextFormulaAudit();
            List<Dictionary<string, object>> reachability = FoundationTraitRangeVNextReachability();
            add("formula_count",GetFoundationTraitRangeVNextFormulas().Count==40&&formulaAudit.Count==43,"Forty calculated formulas and three native locks cover all 43 foundation traits.",formulaAudit.Count);
            add("weights_total_100",formulaAudit.All(row=>Math.Abs(ReadDouble(row,"weightPercent",0d)-100d)<.000001d),"Every formula's absolute contributor weights total 100 percent.",formulaAudit.Where(row=>Math.Abs(ReadDouble(row,"weightPercent",0d)-100d)>=.000001d).ToList());
            add("context_cap",formulaAudit.All(row=>ReadDouble(row,"contextWeightPercent",0d)<=25.000001d),"Context weight never exceeds 25 percent, regardless of direction.",formulaAudit.Where(row=>ReadDouble(row,"contextWeightPercent",0d)>25.000001d).ToList());
            FoundationTraitRangeFormulaVNext positiveContext=FoundationFormulaVNext("context_positive_test",FoundationTermVNext("native.honor",.5d),FoundationTermVNext("context.isLord",.5d,true));
            FoundationTraitRangeFormulaVNext negativeContext=FoundationFormulaVNext("context_negative_test",FoundationTermVNext("native.honor",.5d),FoundationTermVNext("context.isLord",-.5d,true));
            double positiveContextScore=EvaluateFoundationFormulaVNext(positiveContext,key=>key=="context.isLord"?1d:0d),negativeContextScore=EvaluateFoundationFormulaVNext(negativeContext,key=>key=="context.isLord"?1d:0d);
            add("context_direction",positiveContextScore>0d&&negativeContextScore<0d&&positiveContext.Terms.Where(term=>term.IsContext).Sum(term=>Math.Abs(term.Weight))<=FoundationTraitRangeVNextContextWeightCap&&negativeContext.Terms.Where(term=>term.IsContext).Sum(term=>Math.Abs(term.Weight))<=FoundationTraitRangeVNextContextWeightCap,"Context evidence can raise or lower a trait while remaining inside the absolute 25 percent cap.",new Dictionary<string,object>{{"positive",positiveContextScore},{"negative",negativeContextScore}});
            Dictionary<string,object> skillAnchors=new Dictionary<string,object>{{"0",SignedSkillSignalVNext(0)},{"50",SignedSkillSignalVNext(50)},{"100",SignedSkillSignalVNext(100)},{"150",SignedSkillSignalVNext(150)},{"200",SignedSkillSignalVNext(200)},{"300",SignedSkillSignalVNext(300)}};
            add("skill_scale",ReadDouble(skillAnchors,"0",9d)==-2d&&ReadDouble(skillAnchors,"50",9d)==-1d&&ReadDouble(skillAnchors,"100",9d)==0d&&ReadDouble(skillAnchors,"150",9d)==1d&&ReadDouble(skillAnchors,"200",9d)==2d&&ReadDouble(skillAnchors,"300",9d)==2d,"Skills use the signed 0..200 scale and clamp above 200.",skillAnchors);
            Dictionary<string,object> clanAnchors=new Dictionary<string,object>{{"0",ClanLevelSignalVNext(0)},{"1",ClanLevelSignalVNext(1)},{"3",ClanLevelSignalVNext(3)},{"4",ClanLevelSignalVNext(4)},{"5",ClanLevelSignalVNext(5)},{"6",ClanLevelSignalVNext(6)},{"8",ClanLevelSignalVNext(8)}};
            add("clan_level_scale",ReadDouble(clanAnchors,"0",9d)==0d&&ReadDouble(clanAnchors,"1",9d)==0d&&ReadDouble(clanAnchors,"3",9d)==0d&&ReadDouble(clanAnchors,"4",9d)==.30d&&ReadDouble(clanAnchors,"5",9d)==.60d&&ReadDouble(clanAnchors,"6",9d)==1d&&ReadDouble(clanAnchors,"8",9d)==1d,"Clan tiers 1 through 3 add no former-lordship bonus; tiers 4, 5, and 6+ supply 30, 60, and 100 percent of the context term.",clanAnchors);
            List<Dictionary<string,object>> allTerms=formulaAudit.SelectMany(row=>ReadDictionaryList(row,"terms")).ToList();
            bool lordshipReplaced=allTerms.Any(term=>ReadString(term,"input","")=="context.clanLevel")&&!allTerms.Any(term=>ReadString(term,"input","")=="context.isLord"||ReadString(term,"input","")=="context.clanTier");
            add("lordship_replaced",lordshipReplaced,"Every former is-lord formula term uses the graduated clan-level context, and the obsolete binary lordship and old clan-tier inputs are absent.",allTerms.Where(term=>ReadString(term,"input","").StartsWith("context.",StringComparison.OrdinalIgnoreCase)).ToList());
            Dictionary<string,object> nativeAnchors=new Dictionary<string,object>{{"minus3",NativeFoundationSignalVNext(-3)},{"minus1",NativeFoundationSignalVNext(-1)},{"zero",NativeFoundationSignalVNext(0)},{"plus1",NativeFoundationSignalVNext(1)},{"plus3",NativeFoundationSignalVNext(3)}};
            add("native_scale",ReadDouble(nativeAnchors,"minus3",9d)==-2d&&ReadDouble(nativeAnchors,"minus1",9d)==-2d&&ReadDouble(nativeAnchors,"zero",9d)==0d&&ReadDouble(nativeAnchors,"plus1",9d)==2d&&ReadDouble(nativeAnchors,"plus3",9d)==2d,"Native Bannerlord traits use their complete signed range: -1 or lower maps to -2, zero remains neutral, and +1 or higher maps to +2.",nativeAnchors);
            Func<int,Dictionary<string,object>> nativeHero = level =>
            {
                Dictionary<string,object> test=FoundationTraitRangeVNextTestHero("foundation_vnext_native_"+level);
                test["isLord"]=false;test["clanTier"]=0;
                test["traits"]=FoundationTraitRangeVNextNativeKeys.ToDictionary(key=>key,key=>(object)level,StringComparer.OrdinalIgnoreCase);
                return test;
            };
            Dictionary<string,object> nativeNegative=nativeHero(-1),nativeNeutral=nativeHero(0),nativePositive=nativeHero(1);
            string nativePositiveBefore=Json.Serialize(ReadDictionary(nativePositive,"traits"));
            Dictionary<string,object> negativeNativeTraits=CalculateFoundationTraitsVNext(nativeNegative),neutralNativeTraits=CalculateFoundationTraitsVNext(nativeNeutral),positiveNativeTraits=CalculateFoundationTraitsVNext(nativePositive);
            bool nativeLocks=new[]{"courage","mercy","generosity"}.All(key=>TraitInt(negativeNativeTraits,key)==-2&&TraitInt(neutralNativeTraits,key)==0&&TraitInt(positiveNativeTraits,key)==2);
            add("native_locked_full_range",nativeLocks,"Courage, Mercy, and Generosity remain native-locked while expanding native -1, 0, and +1 to foundation -2, 0, and +2.",new Dictionary<string,object>{{"negative",negativeNativeTraits},{"neutral",neutralNativeTraits},{"positive",positiveNativeTraits}});
            add("native_provenance_unchanged",nativePositiveBefore==Json.Serialize(ReadDictionary(nativePositive,"traits")),"Native source values remain unchanged after vNext calculation.",ReadDictionary(nativePositive,"traits"));
            Dictionary<string,object> neutralPercentages=new Dictionary<string,object>(StringComparer.OrdinalIgnoreCase),positivePercentages=new Dictionary<string,object>(StringComparer.OrdinalIgnoreCase);
            foreach(string key in CoreTraitKeys)
            {
                int oldPercentage=StableTraitPercentage("foundation_vnext_downstream",key,0);
                neutralPercentages[key]=oldPercentage;
                positivePercentages[key]=RemapTraitPercentage(oldPercentage,0,TraitInt(positiveNativeTraits,key));
            }
            Dictionary<string,object> neutralVirtues=CalculateCourtVirtues(neutralPercentages),positiveVirtues=CalculateCourtVirtues(positivePercentages),positiveVirtuesRepeat=CalculateCourtVirtues(new Dictionary<string,object>(positivePercentages,StringComparer.OrdinalIgnoreCase));
            bool downstreamPercentages=new[]{"courage","mercy","generosity"}.All(key=>ReadInt(positivePercentages,key,0)>=81&&ReadInt(positivePercentages,key,0)<=100);
            bool downstreamVirtues=Json.Serialize(positiveVirtues)==Json.Serialize(positiveVirtuesRepeat)&&CourtVirtueKeys.Any(key=>ReadInt(neutralVirtues,key,50)!=ReadInt(positiveVirtues,key,50));
            add("downstream_projection",downstreamPercentages&&downstreamVirtues,"Projected five-band percentages and all seven court groups deterministically consume the expanded vNext foundation levels.",new Dictionary<string,object>{{"percentages",positivePercentages},{"neutralVirtues",neutralVirtues},{"positiveVirtues",positiveVirtues}});
            Dictionary<string,FoundationTraitRangeFormulaVNext> formulas=GetFoundationTraitRangeVNextFormulas();
            List<string> evaluationOrder=FoundationTraitRangeVNextEvaluationOrder();
            Dictionary<string,int> evaluationIndex=evaluationOrder.Select((key,index)=>new{key,index}).ToDictionary(item=>item.key,item=>item.index,StringComparer.OrdinalIgnoreCase);
            List<Dictionary<string,object>> dependencyErrors=formulas.Values.SelectMany(formula=>formula.Terms.Where(term=>term.InputKey.StartsWith("trait.",StringComparison.OrdinalIgnoreCase)).Select(term=>new Dictionary<string,object>{{"trait",formula.TraitKey},{"dependency",term.InputKey.Substring("trait.".Length)},{"valid",evaluationIndex.ContainsKey(formula.TraitKey)&&evaluationIndex.ContainsKey(term.InputKey.Substring("trait.".Length))&&evaluationIndex[term.InputKey.Substring("trait.".Length)]<evaluationIndex[formula.TraitKey]}})).Where(row=>!ReadBool(row,"valid",false)).ToList();
            add("dependency_order",dependencyErrors.Count==0&&evaluationOrder.Count==CoreTraitKeys.Length&&evaluationOrder.Distinct(StringComparer.OrdinalIgnoreCase).Count()==CoreTraitKeys.Length,"Every derived-trait contributor is evaluated before the formula that consumes it.",dependencyErrors);
            Func<string,string,bool,bool> hasTerm=(trait,input,inverse)=>formulas[trait].Terms.Any(term=>term.InputKey.Equals(input,StringComparison.OrdinalIgnoreCase)&&(term.Weight<0d)==inverse);
            bool revisedTerms=hasTerm("jealousy","trait.confidence",true)&&(hasTerm("jealousy","trait.socialTrust",true)||hasTerm("jealousy","trait.emotionalStability",true))
                &&hasTerm("confidence","trait.fearfulness",true)&&hasTerm("shame","trait.sociability",true)&&hasTerm("shame","trait.confidence",true)
                &&hasTerm("dutyMotivation","trait.discipline",false)&&hasTerm("dutyMotivation","trait.impulsiveness",true)
                &&hasTerm("traditionalism","trait.curiosity",true)&&hasTerm("traditionalism","trait.knowledgeMotivation",true);
            add("revised_contributors",revisedTerms,"The agreed direct and inverse contributors are present with the intended directions.",new[]{"jealousy","confidence","shame","dutyMotivation","traditionalism"}.Select(trait=>formulaAudit.First(row=>ReadString(row,"trait","")==trait)).ToList());
            List<FoundationTraitRangeTermVNext> nativeTerms=formulas.Values.SelectMany(formula=>formula.Terms).Where(term=>term.InputKey.StartsWith("native.",StringComparison.OrdinalIgnoreCase)).ToList();
            bool nativeTermKeys=nativeTerms.Count>0&&nativeTerms.All(term=>FoundationTraitRangeVNextNativeKeys.Contains(term.InputKey.Substring("native.".Length),StringComparer.OrdinalIgnoreCase));
            FoundationTraitRangeTermVNext inverseNativeTerm=nativeTerms.FirstOrDefault(term=>term.Weight<0d);
            bool inverseNative=inverseNativeTerm!=null&&inverseNativeTerm.Weight*NativeFoundationSignalVNext(1)<0d&&inverseNativeTerm.Weight*NativeFoundationSignalVNext(-1)>0d;
            add("native_contributors",nativeTermKeys&&inverseNative,"Every native contributor is routed through one of the five converted native signals, and inverse native weights preserve their direction.",new Dictionary<string,object>{{"termCount",nativeTerms.Count},{"inverseExample",inverseNativeTerm==null?null:(object)inverseNativeTerm.InputKey}});
            add("all_levels_reachable",reachability.All(row=>ReadBool(row,"reachable",false)),"Every foundation trait can occupy -2, -1, 0, +1, and +2 through valid generated evidence or an explicit Character Editor override.",reachability.Where(row=>!ReadBool(row,"reachable",false)).ToList());
            add("calculated_reachability_reported",reachability.All(row=>row.ContainsKey("calculatedReachable")&&row.ContainsKey("calculatedLevels")),"Generated-input reachability is reported separately so editor-only levels are never presented as formula-generated witnesses.",reachability.Where(row=>!ReadBool(row,"calculatedReachable",false)).ToList());
            Dictionary<string,object> flirt=formulaAudit.First(row=>ReadString(row,"trait","")=="flirtatiousness");List<Dictionary<string,object>> flirtTerms=ReadDictionaryList(flirt,"terms");
            bool flirtWeights=flirtTerms.Count==3
                &&Math.Abs(ReadDouble(flirtTerms.First(x=>ReadString(x,"input","")=="native.calculating"),"weightPercent",0d)-55d)<.000001d
                &&Math.Abs(ReadDouble(flirtTerms.First(x=>ReadString(x,"input","")=="skill.charm"),"weightPercent",0d)-30d)<.000001d
                &&Math.Abs(ReadDouble(flirtTerms.First(x=>ReadString(x,"input","")=="native.generosity"),"weightPercent",0d)-15d)<.000001d;
            add("flirtatiousness",flirtWeights&&ReadBool(reachability.First(row=>ReadString(row,"trait","")=="flirtatiousness"),"reachable",false),"Flirtatiousness is 55 percent native Calculating, 30 percent signed Charm, and 15 percent native Generosity with all levels reachable.",flirt);
            Dictionary<string,XElement> skillSets=new Dictionary<string,XElement>(StringComparer.OrdinalIgnoreCase)
            {
                ["foundation_vnext_skill_template"]=XElement.Parse("<SkillSet id='foundation_vnext_skill_template'><skill id='Charm' value='140'/><skill id='Leadership' value='120'/></SkillSet>")
            };
            XElement emptySkillsNpc=XElement.Parse("<NPCCharacter skill_template='SkillSet.foundation_vnext_skill_template'><skills /></NPCCharacter>");
            XElement partialSkillsNpc=XElement.Parse("<NPCCharacter skill_template='SkillSet.foundation_vnext_skill_template'><skills><skill id='Charm' value='180'/></skills></NPCCharacter>");
            Dictionary<string,object> emptyResolved=ResolveCatalogNativeSkills(emptySkillsNpc,skillSets),partialResolved=ResolveCatalogNativeSkills(partialSkillsNpc,skillSets);
            bool skillMerge=ReadInt(emptyResolved,"charm",0)==140&&ReadInt(emptyResolved,"leadership",0)==120&&ReadInt(partialResolved,"charm",0)==180&&ReadInt(partialResolved,"leadership",0)==120;
            add("skill_template_merge",skillMerge,"Skill templates populate empty skill blocks and direct NPC skills override individual template values without discarding the remaining template.",new Dictionary<string,object>{{"empty",emptyResolved},{"partial",partialResolved}});
            Dictionary<string,object> hero=FoundationTraitRangeVNextTestHero("foundation_vnext_a"),familyHero=new Dictionary<string,object>(hero,StringComparer.OrdinalIgnoreCase){{"spouseId","spouse"},{"fatherId","father"},{"motherId","mother"}};
            string withoutFamily=Json.Serialize(CalculateFoundationTraitsVNext(hero)),withFamily=Json.Serialize(CalculateFoundationTraitsVNext(familyHero));
            add("family_invariant",withoutFamily==withFamily,"Spouse and parent IDs do not influence any vNext foundation calculation.",new Dictionary<string,object>{{"without",withoutFamily},{"with",withFamily}});
            Dictionary<string,object> clanZeroLord=new Dictionary<string,object>(hero,StringComparer.OrdinalIgnoreCase);clanZeroLord["isLord"]=true;clanZeroLord["isWanderer"]=true;clanZeroLord["clanTier"]=0;
            Dictionary<string,object> clanZeroNonLord=new Dictionary<string,object>(clanZeroLord,StringComparer.OrdinalIgnoreCase);clanZeroNonLord["isLord"]=false;
            add("wanderer_no_lord_bonus",Json.Serialize(CalculateFoundationTraitsVNext(clanZeroLord))==Json.Serialize(CalculateFoundationTraitsVNext(clanZeroNonLord)),"Binary lordship cannot give a clanless wanderer any former-lordship bonus.",null);
            List<double> clanAmbitionRaw=new List<double>();
            foreach(int tier in new[]{3,4,5,6})
            {
                Dictionary<string,object> clanHero=new Dictionary<string,object>(hero,StringComparer.OrdinalIgnoreCase);clanHero["clanTier"]=tier;
                CalculateFoundationTraitsVNext(clanHero,out Dictionary<string,double> clanRaw);clanAmbitionRaw.Add(clanRaw["ambition"]);
            }
            add("clan_level_formula_effect",clanAmbitionRaw[0]<clanAmbitionRaw[1]&&clanAmbitionRaw[1]<clanAmbitionRaw[2]&&clanAmbitionRaw[2]<clanAmbitionRaw[3],"Otherwise identical evidence receives progressively stronger clan-level context at tiers 4, 5, and 6.",clanAmbitionRaw);
            Dictionary<string,object> otherId=new Dictionary<string,object>(hero,StringComparer.OrdinalIgnoreCase);
            otherId["heroStringId"]="foundation_vnext_b";
            add("identity_invariant",withoutFamily==Json.Serialize(CalculateFoundationTraitsVNext(otherId)),"Character IDs do not nudge vNext foundation levels.",null);
            Dictionary<string,object> active=BuildTraitDocument(hero);
            Dictionary<string,object> activeFoundation=ReadDictionary(active,"foundationTraits")??new Dictionary<string,object>();
            add("active_model_alignment",Json.Serialize(activeFoundation)==withoutFamily
                &&ReadString(active,"model","")=="reign_core_personality_43_percent_v2"
                &&FoundationTraitModelCurrent(active)
                &&CoreTraitDocumentVersion==5,
                "The weighted full-range framework is the active foundation model and feeds the current percentage document.",
                new Dictionary<string,object>{{"model",ReadString(active,"model","")},{"version",ReadInt(active,"version",0)},{"foundationModel",ReadDictionary(active,"foundationTraitModel")}});
            return rows;
        }

        private static string FoundationTraitRangeVNextFileHash(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return "";
            using (SHA256 sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(path))).Replace("-","").ToLowerInvariant();
        }

        private static Dictionary<string, object> AuditFoundationTraitRangeVNext(string[] args)
        {
            string beforeHash = FoundationTraitRangeVNextFileHash(CharacterProfileCatalogPath);
            List<Dictionary<string, object>> tests = RunFoundationTraitRangeVNextSelfTests();
            List<Dictionary<string, object>> formulas = FoundationTraitRangeVNextFormulaAudit();
            List<Dictionary<string, object>> reachability = FoundationTraitRangeVNextReachability();
            string sandboxData = ProfileArg(args,"--sandbox-data","");
            Dictionary<string, object> projection = FoundationTraitRangeVNextProjection(sandboxData);
            Dictionary<string,object> skillResolution=ReadDictionary(projection,"skillResolution")??new Dictionary<string,object>();
            bool projectionComplete=ReadBool(projection,"complete",false)&&ReadBool(skillResolution,"complete",false);
            tests.Add(new Dictionary<string, object>{{"name","foundation_vnext_projection_complete"},{"passed",projectionComplete},{"detail","The observed catalog projection resolves all required skill templates instead of silently substituting zero skill evidence."},{"data",skillResolution}});
            int profileCount=ReadInt(projection,"profileCount",0);
            List<Dictionary<string,object>> foundationDistribution=ReadDictionaryList(projection,"foundationDistribution");
            Dictionary<string,object> flirtRow=foundationDistribution.FirstOrDefault(row=>ReadString(row,"trait","")=="flirtatiousness")??new Dictionary<string,object>();
            Dictionary<string,object> flirtProjected=ReadDictionary(flirtRow,"projected")??new Dictionary<string,object>();
            bool flirtRegression=profileCount==1117&&ReadInt(flirtProjected,"minus2",-1)==32&&ReadInt(flirtProjected,"minus1",-1)==174&&ReadInt(flirtProjected,"zero",-1)==425&&ReadInt(flirtProjected,"plus1",-1)==340&&ReadInt(flirtProjected,"plus2",-1)==146;
            tests.Add(new Dictionary<string, object>{{"name","foundation_vnext_flirt_catalog_projection"},{"passed",flirtRegression},{"detail","The 1,117-profile catalog projects Flirtatiousness with continuous signed Charm signals and full-range native Calculating and Generosity."},{"data",flirtProjected}});
            Dictionary<string,Dictionary<int,int>> lockedExpected=new Dictionary<string,Dictionary<int,int>>(StringComparer.OrdinalIgnoreCase)
            {
                ["courage"]=new Dictionary<int,int>{{-2,155},{0,658},{2,304}},
                ["mercy"]=new Dictionary<int,int>{{-2,258},{0,662},{2,197}},
                ["generosity"]=new Dictionary<int,int>{{-2,236},{0,565},{2,316}}
            };
            bool lockedCatalog=profileCount==1117&&lockedExpected.All(pair=>
            {
                Dictionary<string,object> row=foundationDistribution.FirstOrDefault(item=>ReadString(item,"trait","")==pair.Key)??new Dictionary<string,object>();
                Dictionary<string,object> counts=ReadDictionary(row,"projected")??new Dictionary<string,object>();
                return ReadInt(counts,"minus2",-1)==pair.Value[-2]&&ReadInt(counts,"minus1",-1)==0&&ReadInt(counts,"zero",-1)==pair.Value[0]&&ReadInt(counts,"plus1",-1)==0&&ReadInt(counts,"plus2",-1)==pair.Value[2];
            });
            List<Dictionary<string,object>> lockedExpectedData=lockedExpected.Select(pair=>new Dictionary<string,object>{{"trait",pair.Key},{"minus2",pair.Value[-2]},{"zero",pair.Value[0]},{"plus2",pair.Value[2]}}).ToList();
            tests.Add(new Dictionary<string, object>{{"name","foundation_vnext_native_locked_catalog_projection"},{"passed",lockedCatalog},{"detail","Observed native-locked catalog traits occupy foundation -2, 0, and +2 with the raw native counts preserved."},{"data",lockedExpectedData}});
            List<Dictionary<string,object>> courtDistribution=ReadDictionaryList(projection,"courtVirtueDistribution");
            bool downstreamComplete=courtDistribution.Count==CourtVirtueKeys.Length&&courtDistribution.All(row=>ReadInt(ReadDictionary(row,"current")??new Dictionary<string,object>(),"count",0)==profileCount&&ReadInt(ReadDictionary(row,"projected")??new Dictionary<string,object>(),"count",0)==profileCount&&row.ContainsKey("transitions"));
            bool foundationDiagnostics=foundationDistribution.Count==CoreTraitKeys.Length&&foundationDistribution.All(row=>row.ContainsKey("transitions")&&row.ContainsKey("calculationRaw")&&row.ContainsKey("observedProjectedLevels"));
            tests.Add(new Dictionary<string, object>{{"name","foundation_vnext_projection_diagnostics"},{"passed",downstreamComplete&&foundationDiagnostics},{"detail","The audit reports foundation transitions and raw ranges plus current and projected court-group distributions and extremes."},{"data",new Dictionary<string,object>{{"foundationRows",foundationDistribution.Count},{"courtRows",courtDistribution.Count}}}});
            HashSet<string> zeroBinExceptions=new HashSet<string>(new[]{"courage","mercy","generosity","religionMotivation"},StringComparer.OrdinalIgnoreCase);
            List<Dictionary<string,object>> emptyCalculatedBins=foundationDistribution.Where(row=>!zeroBinExceptions.Contains(ReadString(row,"trait",""))).Select(row=>
            {
                Dictionary<string,object> counts=ReadDictionary(row,"projected")??new Dictionary<string,object>();
                List<string> empty=new List<string>();
                if(ReadInt(counts,"minus2",0)==0)empty.Add("-2");if(ReadInt(counts,"minus1",0)==0)empty.Add("-1");if(ReadInt(counts,"zero",0)==0)empty.Add("0");if(ReadInt(counts,"plus1",0)==0)empty.Add("+1");if(ReadInt(counts,"plus2",0)==0)empty.Add("+2");
                return new Dictionary<string,object>{{"trait",ReadString(row,"trait","")},{"emptyLevels",empty},{"projected",counts}};
            }).Where(row=>((List<string>)row["emptyLevels"]).Count>0).ToList();
            tests.Add(new Dictionary<string, object>{{"name","foundation_vnext_used_traits_populate_all_levels"},{"passed",emptyCalculatedBins.Count==0},{"detail","Every used calculated foundation trait has evidence-driven catalog characters at -2, -1, 0, +1, and +2; native locks and currently unused Religion Motivation are explicit exceptions."},{"data",emptyCalculatedBins}});
            string afterHash = FoundationTraitRangeVNextFileHash(CharacterProfileCatalogPath);
            bool unchanged = beforeHash == afterHash;
            tests.Add(new Dictionary<string, object>{{"name","foundation_vnext_catalog_read_only"},{"passed",unchanged},{"detail","The audit does not rewrite the shipped profile catalog."},{"data",new Dictionary<string,object>{{"before",beforeHash},{"after",afterHash}}}});
            return new Dictionary<string, object>
            {
                ["ok"] = tests.Count>0&&tests.All(row=>ReadBool(row,"passed",false)),
                ["modelId"] = FoundationTraitRangeVNextModelId,
                ["active"] = true,
                ["writesProfiles"] = false,
                ["calculatedFormulaCount"] = GetFoundationTraitRangeVNextFormulas().Count,
                ["nativeLockedTraits"] = new[] { "courage","mercy","generosity" },
                ["nativeTraitScale"] = "native -1 or lower -> foundation signal -2; native 0 -> 0; native +1 or higher -> +2",
                ["skillScale"] = "signal = clamp((skill - 100) / 50, -2, 2); 200+ clamps to +2",
                ["contextAbsoluteWeightCapPercent"] = 25,
                ["downstreamProjection"] = "active foundation levels -> persistent five-band percentage model v2 -> court virtue model v3; audit remains read-only",
                ["tests"] = tests,
                ["formulas"] = formulas,
                ["reachability"] = reachability,
                ["projection"] = projection
            };
        }
    }
}
