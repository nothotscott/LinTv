namespace LinTv.Mpeg
{
    /// A programme rating. System is the XMLTV rating system ("VCHIP", "MPAA"), or null when
    /// only the broadcaster's free-text label is known.
    public sealed record ContentRating(string? System, string Value);

    /// An event's main audio, in increasing order of richness so the widest of several audio
    /// services wins.
    public enum AudioLayout
    {
        Mono,
        /// 1+1: two independent mono channels, e.g. two languages.
        DualMono,
        Stereo,
        /// Two channels carrying Dolby Surround (matrix) audio.
        MatrixSurround,
        /// More than two channels, e.g. 5.1.
        Surround
    }

    /// What an EIT event's descriptors say about it. Premiere and Repeat come from the
    /// "Premier" and "Repeat" genre codes, which describe the airing rather than the
    /// programme, so they aren't in Genres.
    public sealed record EventDetails(
        IReadOnlyList<string> Genres, IReadOnlyList<ContentRating> Ratings,
        bool Premiere, bool Repeat, bool Captions, AudioLayout? Audio);

    /// The EIT event descriptors LinTv reads. Descriptors it doesn't know are skipped.
    public static class PsipDescriptors
    {
        public const byte Ac3AudioTag = 0x81, CaptionServiceTag = 0x86, ContentAdvisoryTag = 0x87, GenreTag = 0xAB;

        /// Table 6.20 codes that become EventDetails flags.
        private const byte PremierGenre = 0x66, RepeatGenre = 0x6E;

        /// US rating region. Its RRT is fixed by CEA-766 and never transmitted (A/65 Table 6.3).
        private const byte UsRatingRegion = 0x01;

        /// Walks a descriptor loop (tag(8) length(8) body). A descriptor whose length overruns
        /// the loop ends the walk, keeping what was found before it.
        public static EventDetails Parse(ReadOnlySpan<byte> loop)
        {
            var genres = new List<string>();
            var ratings = new List<ContentRating>();
            bool premiere = false, repeat = false, captions = false;
            AudioLayout? audio = null;

            int pos = 0;
            while (pos + 2 <= loop.Length)
            {
                byte tag = loop[pos];
                int length = loop[pos + 1];
                pos += 2;
                if (pos + length > loop.Length) break;
                var body = loop.Slice(pos, length);
                pos += length;

                switch (tag)
                {
                    case GenreTag:
                        ParseGenre(body, genres, ref premiere, ref repeat);
                        break;
                    case ContentAdvisoryTag:
                        ParseContentAdvisory(body, ratings);
                        break;
                    case CaptionServiceTag:
                        // A/65 6.9.2: rsvd(3) number_of_services(5), then 6 bytes per service.
                        // The descriptor is only sent for events that have captions.
                        if (!body.IsEmpty && (body[0] & 0x1F) > 0) captions = true;
                        break;
                    case Ac3AudioTag:
                        // A/65 6.9.1.1: one per audio service, so an event can have several.
                        if (Ac3Layout(body) is { } layout && (audio is null || layout > audio)) audio = layout;
                        break;
                }
            }

            return new EventDetails(genres, ratings, premiere, repeat, captions, audio);
        }

        /// A/65 6.9.13: rsvd(3) attribute_count(5) attribute(8) x count. Each attribute is a
        /// Table 6.20 code; reserved and null codes are skipped.
        private static void ParseGenre(ReadOnlySpan<byte> body, List<string> genres, ref bool premiere, ref bool repeat)
        {
            if (body.IsEmpty) return;
            int count = Math.Min(body[0] & 0x1F, body.Length - 1);
            for (int i = 1; i <= count; i++)
            {
                if (body[i] == PremierGenre) { premiere = true; continue; }
                if (body[i] == RepeatGenre) { repeat = true; continue; }

                var name = GenreName(body[i]);
                if (name is not null && !genres.Contains(name)) genres.Add(name);
            }
        }

        /// A/52 Annex A, Table A4.1: sample_rate_code(3) bsid(5) bit_rate_code(6)
        /// surround_mode(2) bsmod(3) num_channels(4) full_svc(1), then optional fields.
        private static AudioLayout? Ac3Layout(ReadOnlySpan<byte> body)
        {
            if (body.Length < 3) return null;
            int surroundMode = body[1] & 0x03;
            int channels = (body[2] >> 1) & 0x0F;

            // Table A4.5: 0xxx is the acmod (front/rear channels); 1xxx is the maximum number
            // of channels, counting LFE. Table A4.4: surround_mode 10 is Dolby Surround encoded.
            return channels switch
            {
                0b0000 => AudioLayout.DualMono,
                0b0001 or 0b1000 => AudioLayout.Mono,
                0b0010 or 0b1001 => surroundMode == 0b10 ? AudioLayout.MatrixSurround : AudioLayout.Stereo,
                >= 0b0011 and <= 0b0111 => AudioLayout.Surround,
                >= 0b1010 and <= 0b1101 => AudioLayout.Surround,
                _ => null
            };
        }

        /// A/65 6.9.3: rsvd(2) rating_region_count(6), then per region: rating_region(8)
        /// rated_dimensions(8) { rating_dimension_j(8) rsvd(4) rating_value(4) }
        /// rating_description_length(8) rating_description_text (multiple_string_structure).
        /// US ratings are decoded from the dimensions, which clients understand ("TV-PG"). Any
        /// other region falls back to the description text, which A/65 says is an abbreviated
        /// on-screen label.
        private static void ParseContentAdvisory(ReadOnlySpan<byte> body, List<ContentRating> ratings)
        {
            if (body.IsEmpty) return;
            int regions = body[0] & 0x3F;
            int pos = 1;

            for (int r = 0; r < regions; r++)
            {
                if (pos + 2 > body.Length) return;
                byte region = body[pos];
                int dimensions = body[pos + 1];
                pos += 2;

                if (pos + dimensions * 2 + 1 > body.Length) return;
                var decoded = new List<ContentRating>();
                for (int d = 0; d < dimensions; d++)
                {
                    int dimension = body[pos], value = body[pos + 1] & 0x0F;
                    pos += 2;
                    if (region == UsRatingRegion && UsRating(dimension, value) is { } rating)
                        decoded.Add(rating);
                }

                int textLength = body[pos];
                pos += 1;
                if (pos + textLength > body.Length) return;
                var text = MultipleStringStructure.Decode(body.Slice(pos, textLength));
                pos += textLength;

                if (decoded.Count == 0 && !string.IsNullOrWhiteSpace(text)) decoded.Add(new ContentRating(null, text));
                foreach (var rating in decoded)
                    if (!ratings.Contains(rating)) ratings.Add(rating);
            }
        }

        /// CEA-766 region 1 dimensions that carry an age rating: 0 "Entire Audience" and
        /// 5 "Children" (TV Parental Guidelines, XMLTV "VCHIP"), and 7 "MPAA". The other
        /// dimensions are content flags (D, L, S, V, FV) that XMLTV has no element for.
        /// Value 0 means unrated, and 1 is "None" / "N/A".
        private static ContentRating? UsRating(int dimension, int value)
        {
            string[] values = dimension switch
            {
                0 => ["", "", "TV-G", "TV-PG", "TV-14", "TV-MA"],
                5 => ["", "TV-Y", "TV-Y7"],
                7 => ["", "", "G", "PG", "PG-13", "R", "NC-17", "X", "NR"],
                _ => []
            };
            if (value >= values.Length || values[value].Length == 0) return null;
            return new ContentRating(dimension == 7 ? "MPAA" : "VCHIP", values[value]);
        }

        /// A/65 Table 6.20, Categorical Genre Code Assignments. 0x20-0x26 are the basic
        /// categories (Movie, News, Sports...), which clients like Jellyfin use to sort
        /// programmes. 0x27-0xAD are detail attributes. Everything else is reserved or null.
        public static string? GenreName(byte code) =>
            code is >= 0x20 and <= 0xAD ? GenreNames[code - 0x20] : null;

        private static readonly string[] GenreNames =
        [
            // 0x20
            "Education", "Entertainment", "Movie", "News", "Religious", "Sports", "Other", "Action",
            "Advertisement", "Animated", "Anthology", "Automobile", "Awards", "Baseball", "Basketball", "Bulletin",
            // 0x30
            "Business", "Classical", "College", "Combat", "Comedy", "Commentary", "Concert", "Consumer",
            "Contemporary", "Crime", "Dance", "Documentary", "Drama", "Elementary", "Erotica", "Exercise",
            // 0x40
            "Fantasy", "Farm", "Fashion", "Fiction", "Food", "Football", "Foreign", "Fund Raiser",
            "Game/Quiz", "Garden", "Golf", "Government", "Health", "High School", "History", "Hobby",
            // 0x50
            "Hockey", "Home", "Horror", "Information", "Instruction", "International", "Interview", "Language",
            "Legal", "Live", "Local", "Math", "Medical", "Meeting", "Military", "Miniseries",
            // 0x60
            "Music", "Mystery", "National", "Nature", "Police", "Politics", "Premier", "Prerecorded",
            "Product", "Professional", "Public", "Racing", "Reading", "Repair", "Repeat", "Review",
            // 0x70
            "Romance", "Science", "Series", "Service", "Shopping", "Soap Opera", "Special", "Suspense",
            "Talk", "Technical", "Tennis", "Travel", "Variety", "Video", "Weather", "Western",
            // 0x80
            "Art", "Auto Racing", "Aviation", "Biography", "Boating", "Bowling", "Boxing", "Cartoon",
            "Children", "Classic Film", "Community", "Computers", "Country Music", "Court", "Extreme Sports", "Family",
            // 0x90
            "Financial", "Gymnastics", "Headlines", "Horse Racing", "Hunting/Fishing/Outdoors", "Independent", "Jazz", "Magazine",
            "Motorcycle Racing", "Music/Film/Books", "News-International", "News-Local", "News-National", "News-Regional", "Olympics", "Original",
            // 0xA0
            "Performing Arts", "Pets/Animals", "Pop", "Rock & Roll", "Sci-Fi", "Self Improvement", "Sitcom", "Skating",
            "Skiing", "Soccer", "Track/Field", "True", "Volleyball", "Wrestling"
        ];
    }
}
