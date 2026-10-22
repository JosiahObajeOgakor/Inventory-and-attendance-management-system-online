using Inventory.Domain;
using static Inventory.Application.AppsEngine.B;

namespace Inventory.Application.AppsEngine;

public static class FinanceApps
{
    public static readonly AppDef[] All =
    [
        new("sign", "Sign", "admin",
                "Send an agreement, quotation or delivery note to be signed from a phone. The signer opens a link, reads it, types their name and signs. Every signature records the time and the signer's network address.",
                [Kind("document", "document", "Documents",
                    [Title("Document"), Text("signerName", "Signer's name", true, true), Mail("signerEmail", "Signer's email", false), Tel("signerPhone", "Signer's phone"), Look("customerId", "Customer", "customer"),
                     Area("body", "What they are signing", true, "The full text. It is shown to the signer exactly as written, and kept as it was when signed."),
                     Ro("signedName", "Signed by", "text", true), Ro("signedAt", "Signed at"), Ro("signedFrom", "Signed from (network address)")],
                    [S("Draft", "info", "Sent"), S("Sent", "warn", "Draft", "Expired"), S("Signed", "ok"), S("Declined", "bad", "Draft"), S("Expired", "info", "Draft")],
                    [A("send", "Send for signature", In("Draft", "Sent"), primary: true), A("link", "Get the signing link", In("Sent"))])],
                "/sign"),

        new("spreadsheet", "Spreadsheet", "admin",
                "Build your own reports. Cells take numbers, text and formulas (=A1+B1, =SUM(A1:A9)), and =FIG(\"receivables\") pulls a live figure from your records.",
                [Kind("sheet", "sheet", "Sheets", [Title("Sheet name"), Json("grid", "Cells")], [S("Saved", "ok")],
                    hint: "Open a sheet to edit it.")]),

        new("esg", "ESG", "admin",
                "Keep the environmental, social and governance measures a bank, investor or large buyer asks for: energy, fuel, waste, staff safety, training, policies. Record a figure each period and print the summary.",
                [Kind("metric", "measure", "Measures",
                    [Title("Indicator", help: "e.g. Diesel used, Lost-time injuries, Staff trained, Board meetings held"), Sel("category", "Category", ["Environmental", "Social", "Governance"], true, true),
                     Num("value", "Value", true, true), Text("unit", "Unit", false, true, "litres, kWh, people, %…"), Date("date", "Period ending", true, true), Num("target", "Target for the year", false, false),
                     Area("note", "Note or evidence")],
                    [S("Recorded", "info", "Verified"), S("Verified", "ok", "Recorded")])]),
    ];
}

public static class WebsiteApps
{
    public static readonly AppDef[] All =
    [
        new("website", "Website Builder", "admin",
                "A simple public website for your business: a home page, your own pages, and your products with their prices, kept up to date from your stock. Write pages in plain text: a blank line starts a new paragraph, a line starting with # is a heading.",
                [Kind("site", "site settings", "Site settings",
                    [Title("Site name", help: "Shown at the top of every page"), Text("tagline", "Tagline", false, true), Area("intro", "Home page welcome"), Tel("phone", "Phone"), Mail("email", "Email"), Text("address", "Address"),
                     Text("accent", "Colour (hex, e.g. #0f1e3d)", false, false, null, "#0f1e3d"), Chk("showCatalogue", "Show my products and prices on the site", list: true)],
                    [S("Published", "ok", "Hidden"), S("Hidden", "info", "Published")], noDelete: true,
                    hint: "One set of settings for the whole site."),
                 Kind("page", "page", "Pages",
                    [Title("Page title"), Code("Page address"), Area("body", "Page content", true), Num("order", "Order in the menu", false, true, "10"), Chk("inMenu", "Show in the menu", list: true)],
                    [S("Draft", "info", "Published"), S("Published", "ok", "Draft")])],
                ""),

        new("events", "Events", "admin",
                "Plan events, publish them on your site, take registrations, and check people in at the door. The registration form is on the public site; payment for paid events is recorded by you when it arrives.",
                [Kind("event", "event", "Events",
                    [Title("Event"), Code("Web address"), Date("date", "Date", true, true), Text("time", "Time", false, true, "e.g. 10am"), Text("venue", "Venue", false, true), Area("description", "Details"),
                     Num("capacity", "Places (0 = no limit)", false, true, "0"), Money("price", "Ticket price (0 = free)", false, true)],
                    [S("Draft", "info", "Published"), S("Published", "ok", "Closed", "Draft"), S("Closed", "warn", "Published")]),
                 Kind("registration", "registration", "Registrations",
                    [Title("Name"), Mail("email", "Email", false, true), Tel("phone", "Phone", false, true), Num("tickets", "Tickets", true, true, "1"), Chk("paid", "Paid", list: true), Ro("source", "Registered")],
                    [S("Registered", "warn", "CheckedIn", "Cancelled"), S("CheckedIn", "ok"), S("Cancelled", "bad", "Registered")], parent: "event")],
                "/events"),

        new("blog", "Blog", "admin",
                "Publish news, tips and offers on your site. Write in plain text: a blank line starts a new paragraph, a line starting with # is a heading.",
                [Kind("post", "post", "Posts",
                    [Title("Headline"), Code("Web address"), Text("author", "Author", false, true), Date("date", "Publish date", false, true), Text("summary", "One-line summary"), Area("body", "Article", true)],
                    [S("Draft", "info", "Published"), S("Published", "ok", "Draft")])],
                "/blog"),

        new("elearning", "eLearning", "admin",
                "Teach customers or train staff. Build a course from lessons, publish it, enrol people, and they work through it from a link. Finishing every lesson earns a printable certificate. Paid courses unlock when you tick that the student has paid.",
                [Kind("course", "course", "Courses",
                    [Title("Course"), Code("Web address"), Area("description", "What students will learn"), Money("price", "Price (0 = free)", false, true)],
                    [S("Draft", "info", "Published"), S("Published", "ok", "Draft")]),
                 Kind("lesson", "lesson", "Lessons",
                    [Title("Lesson"), Num("order", "Order", true, true, "1"), Area("body", "Lesson content", true, "Plain text. A blank line starts a new paragraph.")],
                    [S("Draft", "info", "Published"), S("Published", "ok", "Draft")], parent: "course"),
                 Kind("enrollment", "student", "Students",
                    [Title("Student's name"), Mail("email", "Email", true, true), Chk("paid", "Paid (for paid courses)", list: true), Ro("done", "Lessons completed", "number", true), Ro("completedIds", "Completed lesson ids", "text"), Ro("certificate", "Certificate no.")],
                    [S("Active", "warn", "Completed"), S("Completed", "ok")], [A("link", "Get the student's link", In("Active", "Completed"))], parent: "course")],
                "/courses"),

        new("forum", "Forum", "admin",
                "A place for customers to ask questions and help each other. Nothing appears on the public site until you approve it, so spam never shows.",
                [Kind("topic", "topic", "Topics",
                    [Title("Question or topic"), Text("author", "Posted by", true, true), Area("body", "Details", true)],
                    [S("Pending", "warn", "Open", "Hidden"), S("Open", "ok", "Closed", "Hidden"), S("Closed", "info", "Open"), S("Hidden", "bad", "Open")]),
                 Kind("reply", "reply", "Replies",
                    [Title("Reply (first words)", false, list: false), Text("author", "Posted by", true, true), Area("body", "Reply", true)],
                    [S("Pending", "warn", "Visible", "Hidden"), S("Visible", "ok", "Hidden"), S("Hidden", "bad", "Visible")], parent: "topic")],
                "/forum"),
    ];
}

public static class PeopleApps
{
    public static readonly AppDef[] All =
    [
        new("recruitment", "Recruitment", "admin",
                "Advertise jobs, track applicants through screening, interview and offer, and turn a hire into an employee record in one click. Open jobs are listed, with an application form, on your public site.",
                [Kind("job", "job", "Jobs",
                    [Title("Job title"), Text("department", "Department", false, true), Text("location", "Location", false, true), Num("openings", "Openings", false, true, "1"), Area("description", "What the job involves", true)],
                    [S("Draft", "info", "Open"), S("Open", "ok", "Closed"), S("Closed", "warn", "Open")]),
                 Kind("applicant", "applicant", "Applicants",
                    [Title("Name"), Tel("phone", "Phone", false, true), Mail("email", "Email", false, true), Sel("rating", "Rating", ["1", "2", "3", "4", "5"], false, true), Text("source", "Where they heard of the job"),
                     Area("notes", "Notes and interview feedback"), Ro("employeeId", "Employee record")],
                    [S("Applied", "info", "Screening", "Rejected"), S("Screening", "warn", "Interview", "Rejected"), S("Interview", "warn", "Offer", "Rejected"), S("Offer", "warn", "Hired", "Rejected"), S("Hired", "ok"), S("Rejected", "bad", "Applied")],
                    [A("hire", "Hire and create the employee", In("Offer"), "Make this person an employee?", true, "Monthly salary (₦)")], parent: "job")],
                "/careers"),

        new("appraisals", "Appraisals", "admin",
                "Regular staff reviews. Open a review cycle, then score each employee from 1 to 5 on four things, set goals and write comments. The overall score is worked out for you, and each employee keeps a history.",
                [Kind("cycle", "review cycle", "Cycles", [Title("Cycle", help: "e.g. 2026 mid-year"), Date("date", "Starts", false, true), Date("endDate", "Ends", false, true)],
                    [S("Open", "warn", "Closed"), S("Closed", "ok", "Open")]),
                 Kind("review", "review", "Reviews",
                    [Title("Review of", false, "Filled in from the employee if blank", false), Look("employeeId", "Employee", "employee", true, true), Text("reviewer", "Reviewer", true, true),
                     Num("quality", "Quality of work (1–5)", true), Num("teamwork", "Teamwork (1–5)", true), Num("punctuality", "Punctuality (1–5)", true), Num("initiative", "Initiative (1–5)", true),
                     Ro("overall", "Overall score", "number", true), Area("goals", "Goals for the next period"), Area("comments", "Comments")],
                    [S("Draft", "info", "Submitted"), S("Submitted", "warn", "Acknowledged", "Draft"), S("Acknowledged", "ok")], parent: "cycle")]),

        new("fleet", "Fleet", "admin",
                "Vehicles, drivers, fuel and servicing in one place. Keep each vehicle's papers and expiry dates (you are warned 30 days ahead), and log fuel and repairs to see what each vehicle costs.",
                [Kind("vehicle", "vehicle", "Vehicles",
                    [Title("Vehicle", help: "e.g. Hilux – Lagos run"), Text("regNo", "Registration number", true, true), Text("makeModel", "Make and model", false, true), Text("driver", "Driver", false, true),
                     Date("insuranceExpiry", "Insurance expires"), Date("roadworthyExpiry", "Road worthiness expires"), Date("licenseExpiry", "Vehicle licence expires"), Num("odometer", "Odometer (km)"), Area("note", "Notes")],
                    [S("Active", "ok", "InService", "Retired"), S("InService", "warn", "Active"), S("Retired", "info", "Active")]),
                 Kind("log", "log entry", "Fuel and repairs",
                    [Title("What", help: "e.g. Diesel, Brake pads, 10,000 km service"), Sel("type", "Type", ["Fuel", "Repair", "Service", "Other"], true, true, "Fuel"), Date("date", "Date", true, true), Money("amount", "Cost", true, true),
                     Num("litres", "Litres", false, true), Num("odometer", "Odometer (km)")],
                    [S("Logged", "ok")], parent: "vehicle")]),
    ];
}

public static class MarketingApps
{
    public static readonly AppDef[] All =
    [
        new("social-marketing", "Social Marketing", "admin",
                "Plan your posts and keep results in one place. This system doesn't log in to your social accounts: when a post is due, open its share link, post it, and mark it Posted.",
                [Kind("post", "post", "Posts",
                    [Title("Post", help: "A short name for you, e.g. Rice promo"), Area("content", "What to post", true), Sel("platform", "Where", ["Facebook", "Instagram", "X", "LinkedIn", "TikTok", "WhatsApp Status"], true, true, "Facebook"),
                     Date("date", "Post on", false, true), Look("productId", "Product it features", "product"), Num("likes", "Likes"), Num("clicks", "Clicks"), Num("sales", "Sales it brought")],
                    [S("Idea", "info", "Scheduled"), S("Scheduled", "warn", "Posted", "Idea"), S("Posted", "ok", "Idea")],
                    [A("share", "Open share link", In("Idea", "Scheduled", "Posted"))])]),

        new("surveys", "Surveys", "admin",
                "Ask customers and staff what they think. Write the questions, share the link (or put it on your site), and read the results. A question per line, with its type after a bar: Was the service fast? | yesno, How would you rate us? | rating, What should we improve? | text, Which do you buy most? | choice: Rice; Beans; Oil.",
                [Kind("survey", "survey", "Surveys",
                    [Title("Survey"), Code("Web address"), Area("intro", "Introduction"), Area("questions", "Questions", true, "One per line: Question | rating  (or yesno, text, choice: A; B; C)")],
                    [S("Draft", "info", "Open"), S("Open", "ok", "Closed"), S("Closed", "warn", "Open")], [A("results", "See results", In("Open", "Closed"), null, true)]),
                 Kind("response", "response", "Responses",
                    [Title("Respondent", false, list: true), Ro("answers", "Answers", "text")], [S("Received", "ok")], parent: "survey", hint: "Responses arrive from the public form; you can delete spam here.")],
                "/survey"),
    ];
}
